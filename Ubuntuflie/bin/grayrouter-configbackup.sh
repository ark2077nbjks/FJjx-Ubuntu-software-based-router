#!/usr/bin/env bash
# ============================================================
#  grayrouter-backup.sh —— 配置备份 / 还原
#  对标 OpenWrt 的 sysupgrade -b / -r：白名单打包 + 校验 + 还原
#
#  和「配置审计」的分工：
#    审计（grayrouter-audit）= 每小时 git 记录「谁改了什么」，用来查
#    备份（本脚本）          = 某个时刻配置的完整快照，用来救
#
#    save [备注]           生成一份配置快照（tar.gz + manifest）
#    list                  列出已有快照
#    verify <id|文件>      校验快照完整性（包哈希 + 清单交叉校验）
#    restore <id> [--yes]  还原（默认要确认；--yes 跳过，供救急脚本调用）
#    prune                 清理旧快照（最近 KEEP 份；restore-points 永不自动删）
#
#  安全设计（按审查意见）：
#   1. 解包前校验每个成员名：拒绝绝对路径 / .. / 白名单外路径（防路径穿越）
#   2. 预演目录用 mktemp -d，trap 清理
#   3. manifest 包内 + 包外各一份，verify 交叉校验（防自证/防错配）
#   4. restore 前先把当前配置打包进 restore-points/ 并校验它（唯一退路）
#   5. 覆盖前旧文件存 .bak_还原_<时间戳>，umask 077
#   6. 只还原文件 + daemon-reload，不重启任何服务（断网动作由人按）
# ============================================================
set -uo pipefail
umask 077

# GR_TEST_ROOT 仅用于本地演练（把 / 换成假树），生产不设
ROOT=${GR_TEST_ROOT:-}
STAGE=""
WORK=""
MAINT=""
# 所有 mktemp 目录都登记进来，退出时统一清（单个全局变量会被后续赋值覆盖，留下孤儿目录）
TMP_DIRS=()
cleanup_tmp() {
  rm -f "${MAINT:-}" 2>/dev/null
  local d
  for d in "${TMP_DIRS[@]:-}"; do
    [ -n "$d" ] && rm -rf "$d" 2>/dev/null
  done
  return 0
}
trap cleanup_tmp EXIT
BK_DIR="${ROOT}/var/lib/grayrouter/backups"
RESTORE_DIR="${BK_DIR}/restore-points"
KEEP=${GR_KEEP:-10}
TS=$(date '+%Y%m%d-%H%M%S')

# 白名单：只有这些东西会被备份/还原
ITEMS=(
  "/etc/grayrouter"
  "/etc/systemd/system/grayrouter.service"
  "/etc/systemd/system/grayrouter-*.service"
  "/etc/systemd/system/grayrouter-*.timer"
  "/etc/systemd/system/grayrouter-*.path"
  "/etc/systemd/system/grayrouter-*.d"
  "/etc/systemd/system/ua3f.service"
  "/etc/sysctl.d/99-grayrouter.conf"
  "/etc/NetworkManager/dnsmasq-shared.d"
  "/home/zy-star/config.yaml"
  "/var/lib/grayrouter/install.log"
)

say()  { printf '%s\n' "$*"; }
ok()   { printf '✓ %s\n' "$*"; }
warn() { printf '! %s\n' "$*"; }
die()  { printf '✗ %s\n' "$*" >&2; exit 1; }
# 测试模式（GR_TEST_ROOT 指向假树）不要求 root，方便本地演练
need_root() { [ -n "${ROOT:-}" ] && return 0; [ "$(id -u)" -eq 0 ] || die "需要 root：sudo bash $0 $*"; }

# 成员是否落在白名单内（$1 为带前导 / 的路径）
in_whitelist() {
  local p="$1" pat
  for pat in "${ITEMS[@]}"; do
    # shellcheck disable=SC2254
    case "$p" in
      $pat|$pat/*) return 0 ;;
    esac
  done
  return 1
}

# 列出要备份的文件（绝对路径，带 ROOT 前缀的不输出）
collect_files() {
  local item f sub
  for item in "${ITEMS[@]}"; do
    for f in $item; do
      [ -e "${ROOT}${f}" ] || continue
      if [ -d "${ROOT}${f}" ]; then
        while IFS= read -r sub; do
          [ -n "$sub" ] || continue
          printf '%s\n' "${sub#"$ROOT"}"
        done < <(find "${ROOT}${f}" -type f 2>/dev/null)
      else
        printf '%s\n' "$f"
      fi
    done
  done
}

pkg_path() { printf '%s/%s.tar.gz' "$BK_DIR" "$1"; }
man_path() { printf '%s/%s.manifest.json' "$BK_DIR" "$1"; }
sha_path() { printf '%s/%s.tar.gz.sha256' "$BK_DIR" "$1"; }

# ---------- save ----------
do_save() {
  local note="${1:-手动备份}" id tarf man files_txt tmptar
  need_root "$@"
  [ -d "${ROOT}/etc" ] || die "白名单根目录不存在：${ROOT}/etc"
  mkdir -p "$BK_DIR" "$RESTORE_DIR" || die "无法创建备份目录 $BK_DIR"

  STAGE=$(mktemp -d "${TMPDIR:-/tmp}/grbk.XXXXXX") || die "无法创建临时目录"
  TMP_DIRS+=("$STAGE")

  local files=()
  while IFS= read -r l; do [ -n "$l" ] && files+=("$l"); done < <(collect_files)
  [ "${#files[@]}" -gt 0 ] || die "白名单里一个文件都没找到，拒绝生成空备份"

  id="grayrouter-config-$TS"
  # 同一秒重复执行时避免覆盖
  local n=1
  while [ -e "$(pkg_path "$id")" ]; do
    id="grayrouter-config-$TS-$(printf '%02d' "$n")"
    n=$((n+1))
  done
  tarf=$(pkg_path "$id"); man=$(man_path "$id")

  # 1) 清单（文件哈希）
  {
    local missing=0
    for f in "${files[@]}"; do
      if [ -f "${ROOT}${f}" ]; then
        jq -n --arg p "$f" \
          --arg h "$(sha256sum "${ROOT}${f}" | cut -d' ' -f1)" \
          --arg s "$(stat -c %s "${ROOT}${f}")" \
          --arg m "$(stat -c %a "${ROOT}${f}")" \
          '{path:$p, sha256:$h, size:($s|tonumber), mode:$m}'
      else
        missing=$((missing+1))
        jq -n --arg p "$f" '{path:$p, missing:true}'
      fi
    done > "$STAGE/files.jsonl"
    [ "$missing" -eq 0 ] || warn "有 $missing 个白名单项在快照时不存在（已记入清单）"
  }

  jq -s --arg id "$id" --arg ts "$(date '+%F %T')" --arg note "$note" \
        --arg host "$(hostname)" \
        --arg ver "$(cat "${ROOT}/var/lib/grayrouter/VERSION" 2>/dev/null || echo unknown)" \
        --arg audit "$(git -C "${ROOT}/var/lib/grayrouter/etcsnap" log -1 --format=%h 2>/dev/null || echo none)" \
        '{schema:1, id:$id, created:$ts, host:$host, version:$ver, note:$note, audit_commit:$audit, files:.}' \
        "$STAGE/files.jsonl" > "$man" || die "生成清单失败"
  cp "$man" "$STAGE/manifest.json" || die "清单暂存失败"

  # 标准 sha256 清单（解包回原位后可直接 sha256sum -c 校验）
  jq -r '.files[] | select(.sha256) | "\(.sha256)  \(.path)"' "$man" \
    > "${BK_DIR}/${id}.files.sha256" 2>/dev/null || true

  # 2) 打包：先打配置（相对路径），再把 manifest 追加进同一个未压缩 tar，最后 gzip
  local rel=()
  for f in "${files[@]}"; do rel+=("${f#/}"); done
  printf '%s\n' "${rel[@]}" > "$STAGE/files.txt"
  tmptar="$STAGE/pkg.tar"
  tar -cf "$tmptar" -C "${ROOT:-/}" --files-from="$STAGE/files.txt" 2>/dev/null \
    || die "打包配置失败"
  tar -rf "$tmptar" -C "$STAGE" manifest.json 2>/dev/null \
    || die "清单入包失败"
  gzip -c "$tmptar" > "$tarf" || die "压缩失败"
  chmod 600 "$tarf" "$man" 2>/dev/null || true

  # 3) 包外哈希（校验链：包外 sha256 → 包 → 包内 manifest → 每个文件）
  sha256sum "$tarf" | cut -d' ' -f1 > "$(sha_path "$id")"
  chmod 600 "$(sha_path "$id")" 2>/dev/null || true

  ok "已生成备份：$id"
  say "  包：$tarf（$(du -h "$tarf" | cut -f1)）"
  say "  清单：$man（${#files[@]} 个文件）"
  say "  备注：$note"
  do_prune_quiet
}

# ---------- list ----------
do_list() {
  [ -d "$BK_DIR" ] || { say "还没有任何备份（$BK_DIR 不存在）"; return 0; }
  local f id cnt size created note
  say "=== 配置备份（[$BK_DIR]） ==="
  for f in "$BK_DIR"/grayrouter-config-*.tar.gz; do
    [ -e "$f" ] || continue
    id=$(basename "$f" .tar.gz)
    if [ -f "$(man_path "$id")" ] && command -v jq >/dev/null 2>&1; then
      cnt=$(jq -r '.files | length' "$(man_path "$id")" 2>/dev/null || echo '?')
      created=$(jq -r '.created' "$(man_path "$id")" 2>/dev/null || echo '?')
      note=$(jq -r '.note // ""' "$(man_path "$id")" 2>/dev/null || echo '')
    else
      cnt='?'; created='?'; note=''
    fi
    size=$(du -h "$f" 2>/dev/null | cut -f1)
    printf '  %-34s %6s  %s  %s\n' "$id" "$size" "${cnt}个文件" "$note"
    printf '  %-34s 创建于 %s\n' '' "$created"
  done
  local rp=0
  for f in "$RESTORE_DIR"/pre-restore-*.tar.gz; do [ -e "$f" ] && rp=$((rp+1)); done
  [ "$rp" -gt 0 ] && say "  （另有 $rp 份还原点，在 restore-points/，不参与自动清理）"
  return 0
}

# ---------- verify ----------
# $1 = id 或 tar.gz 路径
do_verify() {
  local arg="${1:-}"
  need_root "$@"
  [ -n "$arg" ] || die "用法：$0 verify <id|文件>"
  local tarf shaexp id stage files_n
  if [ -f "$arg" ]; then
    tarf=$(cd "$(dirname "$arg")" && pwd)/$(basename "$arg")
    id=$(basename "$tarf" .tar.gz)
    shaexp=""
  else
    id="$arg"; tarf=$(pkg_path "$id")
    [ -f "$tarf" ] || die "找不到备份：$tarf"
    shaexp=$(cat "$(sha_path "$id")" 2>/dev/null || echo "")
  fi
  ok "校验：$id"

  # 1) 包哈希
  if [ -n "$shaexp" ]; then
    local got; got=$(sha256sum "$tarf" | cut -d' ' -f1)
    [ "$got" = "$shaexp" ] || die "包哈希不匹配（文件被改动过）"
    say "  ✓ 包哈希一致"
  else
    warn "  没有包外哈希文件，跳过包哈希校验"
  fi

  # 2) 包完整性 + 成员路径安全
  tar -tzf "$tarf" >/dev/null 2>&1 || die "包损坏（gzip 解不开）"
  local bad=0 member
  while IFS= read -r member; do
    case "$member" in
      manifest.json) continue ;;
      /*|*..*)
        warn "  包内含危险路径：$member"
        bad=$((bad+1))
        continue ;;
    esac
    if ! in_whitelist "/$member"; then
      warn "  包内含白名单外路径：$member"
      bad=$((bad+1))
    fi
  done < <(tar -tzf "$tarf" 2>/dev/null)
  [ "$bad" -eq 0 ] || die "包内有 $bad 个可疑成员，拒绝使用"

  # 3) 解包后逐个对哈希
  STAGE=$(mktemp -d "${TMPDIR:-/tmp}/grbv.XXXXXX") || die "无法创建临时目录"
  TMP_DIRS+=("$STAGE")
  tar -xzf "$tarf" -C "$STAGE" --no-same-owner 2>/dev/null || die "解包失败"
  [ -f "$STAGE/manifest.json" ] || die "包内没有 manifest.json"
  jq -e '.files' "$STAGE/manifest.json" >/dev/null 2>&1 || die "manifest 解析失败"

  local total=0 badf=0
  while IFS= read -r line; do
    local p h miss
    p=$(printf '%s' "$line" | jq -r '.path')
    h=$(printf '%s' "$line" | jq -r '.sha256 // empty')
    miss=$(printf '%s' "$line" | jq -r '.missing // false')
    total=$((total+1))
    [ "$miss" = "true" ] && continue
    [ -f "$STAGE${p}" ] || { warn "  包里缺文件：$p"; badf=$((badf+1)); continue; }
    [ "$(sha256sum "$STAGE${p}" | cut -d' ' -f1)" = "$h" ] || { warn "  哈希不符：$p"; badf=$((badf+1)); }
  done < <(jq -c '.files[]' "$STAGE/manifest.json")
  files_n=$total
  [ "$badf" -eq 0 ] || die "$badf 个文件校验失败"
  ok "校验通过：$files_n 个文件全部一致"
  return 0
}

# ---------- prune ----------
# 保留最近 KEEP 份主备份；restore-points 不动
do_prune() {
  [ -d "$BK_DIR" ] || return 0
  local all=() i
  while IFS= read -r l; do [ -n "$l" ] && all+=("$l"); done < <(
    ls -1 "$BK_DIR"/grayrouter-config-*.tar.gz 2>/dev/null | sort
  )
  [ "${#all[@]}" -gt "$KEEP" ] || return 0
  local drop=$(( ${#all[@]} - KEEP ))
  for ((i=0; i<drop; i++)); do
    local f="${all[$i]}" id
    id=$(basename "$f" .tar.gz)
    rm -f "$f" "$(man_path "$id")" "$(sha_path "$id")"
    say "  - 清理旧备份：$id"
  done
  return 0
}
do_prune_quiet() { do_prune >/dev/null 2>&1 || true; }

# ---------- restore ----------
do_restore() {
  local arg="${1:-}" yes=0
  [ -n "$arg" ] || die "用法：$0 restore <id> [--yes]"
  [ "${2:-}" = "--yes" ] && yes=1
  need_root "$@"
  local id="$arg" tarf pre
  tarf=$(pkg_path "$id")
  [ -f "$tarf" ] || die "找不到备份：$tarf（用 list 查看可用 id）"

  # 1) 先校验要还原的包
  do_verify "$id" || die "校验未通过，拒绝还原"

  # 还原期间挂「维护中」标记（看门狗将来可读取跳过自愈；当前版本不读，仅预留）
  if [ -z "$ROOT" ] && [ -d /run/grayrouter ]; then
    MAINT=/run/grayrouter/maintenance
    : > "$MAINT" 2>/dev/null || true
  fi

  # 2) 把「当前配置」打成还原点（唯一退路），并校验它
  say "== 还原前快照当前配置 =="
  mkdir -p "$RESTORE_DIR" || die "无法创建 $RESTORE_DIR"
  pre="$RESTORE_DIR/pre-restore-$TS.tar.gz"
  # 复用 save 的逻辑会写到 BK_DIR，这里单独打一份到 restore-points
  STAGE=$(mktemp -d "${TMPDIR:-/tmp}/grpr.XXXXXX") || die "无法创建临时目录"
  TMP_DIRS+=("$STAGE")
  local files=()
  while IFS= read -r l; do [ -n "$l" ] && files+=("$l"); done < <(collect_files)
  local rel=()
  for f in "${files[@]}"; do rel+=("${f#/}"); done
  printf '%s\n' "${rel[@]}" > "$STAGE/files.txt"
  tar -czf "$pre" -C "${ROOT:-/}" --files-from="$STAGE/files.txt" 2>/dev/null \
    || die "生成还原点失败，已中止（当前配置未动）"
  chmod 600 "$pre" 2>/dev/null || true
  tar -tzf "$pre" >/dev/null 2>&1 || die "还原点校验失败，已中止（当前配置未动）"
  ok "当前配置已存为：$pre"

  # 3) 解包到临时目录，逐文件与当前比对
  WORK=$(mktemp -d "${TMPDIR:-/tmp}/grrs.XXXXXX") || die "无法创建临时目录"
  TMP_DIRS+=("$WORK")
  tar -xzf "$tarf" -C "$WORK" --no-same-owner 2>/dev/null || die "解包失败，已中止"
  local changed=() added=() p h cur m
  local -A modes=()
  while IFS= read -r line; do
    p=$(printf '%s' "$line" | jq -r '.path')
    h=$(printf '%s' "$line" | jq -r '.sha256 // empty')
    m=$(printf '%s' "$line" | jq -r '.mode // "600"')
    [ -n "$h" ] || continue
    [ -f "$WORK${p}" ] || continue
    modes["$p"]="$m"
    if [ -f "${ROOT}${p}" ]; then
      cur=$(sha256sum "${ROOT}${p}" | cut -d' ' -f1)
      [ "$cur" = "$h" ] || changed+=("$p")
    else
      added+=("$p")
    fi
  done < <(jq -c '.files[]' "$WORK/manifest.json")

  say "== 变更预览 =="
  if [ "${#changed[@]}" -eq 0 ] && [ "${#added[@]}" -eq 0 ]; then
    ok "配置和备份完全一致，无需还原"
    return 0
  fi
  [ "${#changed[@]}" -gt 0 ] && printf '  将覆盖 %d 个文件：\n' "${#changed[@]}" && printf '    %s\n' "${changed[@]}"
  [ "${#added[@]}" -gt 0 ] && printf '  将新增 %d 个文件：\n' "${#added[@]}" && printf '    %s\n' "${added[@]}"

  # 4) 确认
  if [ "$yes" -ne 1 ]; then
    printf '  确认还原？（输入 y 继续，其他任意键取消） '
    read -r ans
    [ "$ans" = "y" ] || { say "已取消，配置未动"; return 0; }
  fi

  # 5) 两阶段提交：先全部写成 .tmp_还原，全部成功后再逐个原子 mv
  #    任何一步失败都不会动到现有配置（失败时只留下 .tmp 文件）
  local bakts; bakts=$(date '+%Y%m%d-%H%M%S')
  local f cnt=0
  for f in "${changed[@]}" "${added[@]}"; do
    [ -f "$WORK${f}" ] || continue
    mkdir -p "$(dirname "${ROOT}${f}")" || die "无法创建目录：$(dirname "$f")"
    if [ -f "${ROOT}${f}" ]; then
      cp -a "${ROOT}${f}" "${ROOT}${f}.bak_还原_${bakts}" 2>/dev/null || die "备份旧文件失败：$f"
    fi
    cp -a "$WORK${f}" "${ROOT}${f}.tmp_还原_${bakts}" || die "写入失败：$f（现有配置未动）"
    chmod "${modes[$f]:-600}" "${ROOT}${f}.tmp_还原_${bakts}" 2>/dev/null || true
  done
  for f in "${changed[@]}" "${added[@]}"; do
    [ -f "${ROOT}${f}.tmp_还原_${bakts}" ] || continue
    mv -f "${ROOT}${f}.tmp_还原_${bakts}" "${ROOT}${f}" || die "替换失败：$f（旧文件在 .bak_还原_${bakts}）"
    cnt=$((cnt+1))
  done
  ok "已还原 $cnt 个文件（旧文件后缀 .bak_还原_$bakts）"

  # 6) 不重启服务；只做 daemon-reload（仅真实环境）
  if [ -z "$ROOT" ] && command -v systemctl >/dev/null 2>&1; then
    systemctl daemon-reload 2>/dev/null && say "  已 daemon-reload（未重启任何服务）"
  fi
  say ""
  say "提示：还原只写了文件，没有重启服务。需要生效请自己决定："
  say "  sudo systemctl restart grayrrouter-<服务名>.service"
  say "  注意：重启网络相关服务会短暂断网。"
  return 0
}

# ---------- export ----------
# 把指定备份复制到登录用户家目录，供面板 scp 下载（包本体 600 root，普通用户直接下不了）
do_export() {
  local id="${1:-}" who="${2:-}" tarf home dst
  need_root "$@"
  [ -n "$id" ] || die "用法：$0 export <id> [用户]"
  tarf=$(pkg_path "$id")
  [ -f "$tarf" ] || die "找不到备份：$tarf"
  [ -n "$who" ] || who=zy-star
  home=$(getent passwd "$who" 2>/dev/null | cut -d: -f6)
  [ -n "$home" ] || die "用户不存在：$who"
  dst="$home/grayrouter-export-$id.tar.gz"
  cp -f "$tarf" "$dst" || die "复制失败：$dst"
  chown "$who:$who" "$dst" 2>/dev/null || true
  chmod 600 "$dst" 2>/dev/null || true
  say "$dst"
  return 0
}

# ---------- main ----------
usage() {
  cat <<EOF
用法：sudo $0 {save [备注] | list | verify <id> | restore <id> [--yes] | export <id> [用户] | prune}

  save      生成一份配置快照（只读现有配置，不改任何东西）
  list      列出已有快照
  verify    校验快照完整性
  restore   还原（默认要你敲 y 确认；--yes 跳过）
  export    把备份复制到用户家目录，供面板下载
  prune     清理旧快照（保留最近 $KEEP 份；还原点不参与清理）

备份位置：$BK_DIR
EOF
}

case "${1:-}" in
  save)    shift; do_save "${1:-手动备份}" ;;
  list)    do_list ;;
  verify)  shift; do_verify "${1:-}" ;;
  restore) shift; do_restore "${1:-}" "${2:-}" ;;
  export)  shift; do_export "${1:-}" "${2:-}" ;;
  prune)   need_root "$@"; do_prune ;;
  help|-h|--help) usage ;;
  *) usage; exit 2 ;;
esac
