#!/usr/bin/env bash
# GrayRouter 公共函数：日志 / 备份 / 操作日志(journal) / 回滚
# shellcheck shell=bash

GR_ETC=${GR_ETC:-/etc/grayrouter}
GR_OPT=${GR_OPT:-/opt/grayrouter}
GR_VAR=${GR_VAR:-/var/lib/grayrouter}
GR_JOURNAL="$GR_VAR/journal.tsv"
GR_LOG="$GR_VAR/install.log"
# 日志目录不可写（未装 / 非 root）就丢弃日志，不能因此报错
if ! { mkdir -p "$GR_VAR" 2>/dev/null && : >>"$GR_LOG" 2>/dev/null; }; then
  GR_LOG=/dev/null
fi

C_R=$'\033[31m'; C_G=$'\033[32m'; C_Y=$'\033[33m'; C_B=$'\033[36m'; C_D=$'\033[2m'; C_N=$'\033[0m'
info()  { printf "  ${C_D}·${C_N} %s\n" "$*"; echo "· $*" >>"$GR_LOG" 2>/dev/null || true; }
ok()    { printf "  ${C_G}✓${C_N} %s\n" "$*"; echo "✓ $*" >>"$GR_LOG" 2>/dev/null || true; }
warn()  { printf "  ${C_Y}!${C_N} %s\n" "$*"; echo "! $*" >>"$GR_LOG" 2>/dev/null || true; }
bad()   { printf "  ${C_R}✗${C_N} %s\n" "$*"; echo "✗ $*" >>"$GR_LOG" 2>/dev/null || true; }
step()  { printf "\n${C_B}【%s】${C_N}\n" "$*"; echo "" >>"$GR_LOG" 2>/dev/null || true; echo "## $*" >>"$GR_LOG" 2>/dev/null || true; }
die()   { bad "$*"; exit 1; }

# ---- 操作日志：每做一件事记一行，卸载时逆序撤销 ----
jinit() {
  mkdir -p "$GR_VAR" 2>/dev/null || true
  : >"$GR_JOURNAL" 2>/dev/null || true
  { echo "# GrayRouter journal  $(date '+%F %T')"; } >>"$GR_JOURNAL"
}
jlog() { printf '%s\t%s\n' "$1" "$2" >>"$GR_JOURNAL"; }

# 备份一个将被改动的文件（幂等：只备份一次）
backup_file() {
  local f=$1
  [ -e "$f" ] || return 0
  local b="${f}.bak_grayrouter_$(date +%Y%m%d_%H%M%S)"
  cp -a "$f" "$b" && jlog FILE_BACKUP "$f|$b" && info "已备份 $f → $(basename "$b")"
}

# 记录新建的文件（卸载时删除）
track_created() { jlog FILE_CREATED "$1"; }
track_dir()     { jlog DIR_CREATED "$1"; }
track_unit()    { jlog SYSTEMD_UNIT "$1"; }
track_nm()      { jlog NM_CON "$1"; }
track_sysctl()  { jlog SYSCTL "$1"; }

have() { command -v "$1" >/dev/null 2>&1; }

need_root() {
  [ "${GR_SANDBOX:-0}" = "1" ] && return 0   # 沙箱自测用，正常安装不生效
  if [ "$(id -u)" -ne 0 ]; then
    printf "${C_R}需要 root。${C_N}请用： ${C_B}sudo bash install.sh${C_N}\n"
    exit 2
  fi
}

confirm() {
  # $1=提示  $2=默认(y/n)
  local ans
  [ "${GR_ASSUME_YES:-0}" = "1" ] && return 0
  read -r -p "$1 [${2}]: " ans || ans=""
  ans=${ans:-$2}
  [ "$ans" = y ] || [ "$ans" = Y ] || [ "$ans" = yes ]
}

ask() {
  # $1=提示 $2=默认值
  local v
  read -r -p "$1${2:+ [$2]}: " v || v=""
  printf '%s' "${v:-$2}"
}
