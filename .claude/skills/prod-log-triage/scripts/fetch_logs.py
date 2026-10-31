#!/usr/bin/env python3
"""拉取 XAUAT.EduApi 生产 /Logs 中 Warning 及以上的日志，聚合并输出 JSON 摘要。

用法:
  LOG_VIEW_TOKEN=xxx python3 fetch_logs.py --hours 24
  python3 fetch_logs.py --level Error --hours 72
  python3 fetch_logs.py --hours 24 --search "登录" --json-out /tmp/logs.json

stdout 输出 JSON（供模型分析），stderr 输出一行人类可读摘要。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path

DEFAULT_BASE_URL = "https://xauatapi.xauat.site"
SHANGHAI = timezone(timedelta(hours=8))
LEVEL_ORDER = {"Fatal": 5, "Error": 4, "Warning": 3}
MIN_TS = datetime.min.replace(tzinfo=timezone.utc)

GUID_RE = re.compile(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")
DATETIME_RE = re.compile(r"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?\b")
NUMBER_RE = re.compile(r"\b\d+(?:\.\d+)?\b")
WS_RE = re.compile(r"\s+")


def die(message: str) -> None:
    print(f"[error] {message}", file=sys.stderr)
    raise SystemExit(1)


def build_ssl_context() -> ssl.SSLContext:
    """macOS 上 Python 常拿不到系统 CA，按 certifi → 系统证书包补齐（仍保持严格校验）。"""
    context = ssl.create_default_context()
    if context.get_ca_certs():
        return context
    candidates = []
    try:
        import certifi  # type: ignore[import-not-found]  # 可选依赖
        candidates.append(certifi.where())
    except ImportError:
        pass
    candidates += [
        "/etc/ssl/cert.pem",
        "/opt/homebrew/etc/openssl@3/cert.pem",
        "/usr/local/etc/openssl@3/cert.pem",
    ]
    for path in candidates:
        if path and os.path.exists(path):
            context.load_verify_locations(cafile=path)
            if context.get_ca_certs():
                return context
    return context


SSL_CONTEXT = build_ssl_context()


def field(obj: dict, *names, default=None):
    for name in names:
        if name in obj:
            return obj[name]
    return default


def find_repo_root() -> Path | None:
    for parent in Path(__file__).resolve().parents:
        if (parent / "XAUAT.EduApi").is_dir() and (parent / ".env.example").is_file():
            return parent
    return None


def read_dotenv_value(path: Path, key: str) -> str | None:
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:
        return None
    for raw in lines:
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        if line.startswith("export "):
            line = line[len("export "):].lstrip()
        name, value = line.split("=", 1)
        if name.strip() == key:
            return value.strip().strip('"').strip("'")
    return None


def resolve_token(args) -> tuple[str | None, str | None]:
    """token 优先级：--token → 环境变量 → 本地 .env 的 MAP_ADMIN_TOKEN。

    生产 LOG_VIEW_TOKEN 与本地 .env 的 MAP_ADMIN_TOKEN 取值相同（已实测）。
    该 token 同时具备 Map 管理端写权限，只在本机读取，不要打印或提交。
    """
    if args.token:
        return args.token.strip(), "--token"
    for name in ("EDUAPI_LOG_TOKEN", "LOG_VIEW_TOKEN"):
        value = os.environ.get(name)
        if value and value.strip():
            return value.strip(), name
    root = find_repo_root()
    if root is not None:
        value = read_dotenv_value(root / "XAUAT.EduApi" / ".env", "MAP_ADMIN_TOKEN")
        if value:
            return value, "XAUAT.EduApi/.env:MAP_ADMIN_TOKEN"
    return None, None


def fetch_page(base_url: str, token: str | None, page: int, page_size: int, level: str, search: str | None):
    params = {"page": page, "pageSize": page_size, "level": level}
    if search:
        params["search"] = search
    url = base_url.rstrip("/") + "/Logs?" + urllib.parse.urlencode(params)
    request = urllib.request.Request(url, headers={"Accept": "application/json"})
    if token:
        request.add_header("X-Log-Token", token)

    try:
        with urllib.request.urlopen(request, timeout=30, context=SSL_CONTEXT) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        if exc.code == 401:
            die("生产 /Logs 返回 401：LOG_VIEW_TOKEN 缺失或不正确（用 EDUAPI_LOG_TOKEN / LOG_VIEW_TOKEN 环境变量或 --token 传入）。")
        if exc.code == 429:
            die("生产 /Logs 返回 429：请求被限流，稍后再试。")
        body = exc.read()[:300].decode("utf-8", "replace")
        die(f"生产 /Logs 返回 HTTP {exc.code}：{body}")
    except urllib.error.URLError as exc:
        die(f"无法连接 {base_url}：{exc.reason}")
    except json.JSONDecodeError as exc:
        die(f"响应不是合法 JSON：{exc}")


def parse_ts(value) -> datetime | None:
    if not value:
        return None
    text = str(value).strip().replace("Z", "+00:00")
    # Python < 3.11 的 fromisoformat 只接受 3 或 6 位小数，先截断多余精度。
    text = re.sub(r"(\.\d{6})\d+", r"\1", text)
    try:
        parsed = datetime.fromisoformat(text)
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed


def entry_time(item: dict) -> datetime | None:
    return parse_ts(field(item, "timestamp", "Timestamp"))


def fmt_cst(value: datetime | None) -> str | None:
    return value.astimezone(SHANGHAI).strftime("%Y-%m-%d %H:%M:%S") if value else None


def truncate(text, limit: int) -> str:
    text = text or ""
    return text if len(text) <= limit else text[: limit - 1] + "…"


def redact(text) -> str:
    """日志里可能混有 cookie/学号等敏感值，输出前做基础脱敏。"""
    if not text:
        return ""
    text = str(text)
    rules = [
        (r"(?i)\b(cookie|set-cookie|authorization|token|password|passwd|pwd|secret)\b\s*[:=]\s*\S+", r"\1=<redacted>"),
        (r"(?i)(jsessionid|castgc|__pstsid__|tgtsso|tgc)\s*=\s*[^;\s,]+", r"\1=<redacted>"),
        (r"\b\d{10}\b", "<student-id>"),
        (r"\b1[3-9]\d{9}\b", "<phone>"),
    ]
    for pattern, repl in rules:
        text = re.sub(pattern, repl, text)
    return text


def normalize(text, limit: int = 220) -> str:
    if not text:
        return ""
    text = GUID_RE.sub("<guid>", str(text))
    text = DATETIME_RE.sub("<time>", text)
    text = NUMBER_RE.sub("<n>", text)
    return WS_RE.sub(" ", text).strip()[:limit]


def dedupe_key(item: dict):
    return (
        field(item, "timestamp", "Timestamp"),
        field(item, "level", "Level"),
        field(item, "message", "Message"),
        field(item, "exception", "Exception"),
    )


def cluster_key(item: dict):
    exception = field(item, "exception", "Exception", default="") or ""
    # 先脱敏再归一化：否则学号/cookie 不同会把同类错误拆成多组。
    exception_head = normalize(redact(exception.splitlines()[0] if exception.strip() else ""))
    return (
        field(item, "level", "Level", default="Unknown"),
        field(item, "source", "Source", default="(unknown)") or "(unknown)",
        exception_head,
        normalize(redact(field(item, "message", "Message", default=""))),
    )


def make_sample(item: dict) -> dict:
    return {
        "time_cst": fmt_cst(entry_time(item)),
        "message": truncate(redact(field(item, "message", "Message", default="")), 600),
        "exception": truncate(redact(field(item, "exception", "Exception", default="")), 1200),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="拉取并聚类 XAUAT.EduApi 生产 Warning+ 日志")
    parser.add_argument("--base-url", default=os.environ.get("EDUAPI_BASE_URL", DEFAULT_BASE_URL))
    parser.add_argument("--level", default="Warning", choices=["Warning", "Error", "Fatal"],
                        help="服务端最低等级；Warning 同时覆盖 Error/Fatal")
    parser.add_argument("--hours", type=float, default=24.0,
                        help="只统计最近 N 小时；0 表示使用服务端可见的全部日志")
    parser.add_argument("--search", default=None, help="服务端关键词过滤")
    parser.add_argument("--page-size", type=int, default=200)
    parser.add_argument("--max-pages", type=int, default=10)
    parser.add_argument("--max-clusters", type=int, default=30)
    parser.add_argument("--token", default=None)
    parser.add_argument("--json-out", default=None)
    args = parser.parse_args()

    if args.page_size < 1 or args.page_size > 200:
        die("--page-size 必须在 1-200 之间（服务端限制）")

    token, token_source = resolve_token(args)
    if token:
        print(f"[info] token 来源：{token_source}", file=sys.stderr)
    else:
        print("[warn] 未找到 token（EDUAPI_LOG_TOKEN / LOG_VIEW_TOKEN / .env:MAP_ADMIN_TOKEN），将尝试匿名访问。",
              file=sys.stderr)

    first = fetch_page(args.base_url, token, 1, args.page_size, args.level, args.search)
    total_reported = int(field(first, "total", "Total", default=0) or 0)
    total_pages = int(field(first, "totalPages", "TotalPages", default=0) or 0)
    if total_pages <= 0 and total_reported:
        total_pages = (total_reported + args.page_size - 1) // args.page_size
    pages_to_fetch = min(total_pages, args.max_pages)

    raw_items = list(field(first, "items", "Items", default=[]) or [])
    for page in range(2, pages_to_fetch + 1):
        payload = fetch_page(args.base_url, token, page, args.page_size, args.level, args.search)
        raw_items.extend(field(payload, "items", "Items", default=[]) or [])

    # 分页期间可能有新日志写入导致页偏移，按内容去重。
    seen = set()
    entries = []
    for item in raw_items:
        key = dedupe_key(item)
        if key in seen:
            continue
        seen.add(key)
        entries.append(item)

    now = datetime.now(timezone.utc)
    cutoff = now - timedelta(hours=args.hours) if args.hours and args.hours > 0 else None

    def in_window(item: dict) -> bool:
        moment = entry_time(item)
        if moment is None:
            return True
        return cutoff is None or moment >= cutoff

    windowed = [item for item in entries if in_window(item)]
    windowed.sort(key=lambda item: entry_time(item) or MIN_TS, reverse=True)

    counts = Counter(field(item, "level", "Level", default="Unknown") for item in windowed)
    times = [entry_time(item) for item in windowed if entry_time(item)]

    source_counter = Counter(
        (field(item, "source", "Source", default="(unknown)") or "(unknown)",
         field(item, "level", "Level", default="Unknown"))
        for item in windowed
    )
    top_sources = [
        {"source": source, "level": level, "count": count}
        for (source, level), count in source_counter.most_common(15)
    ]

    buckets = defaultdict(Counter)
    for item in windowed:
        moment = entry_time(item)
        if moment is None:
            continue
        hour = moment.astimezone(SHANGHAI).replace(minute=0, second=0, microsecond=0)
        buckets[hour][field(item, "level", "Level", default="Unknown")] += 1
    timeline = [
        {"hour": hour.strftime("%m-%d %H:00"), "total": sum(counter.values()),
         "by_level": dict(counter)}
        for hour, counter in sorted(buckets.items())
    ][-48:]

    grouped = defaultdict(list)
    for item in windowed:
        grouped[cluster_key(item)].append(item)

    clusters = []
    for (level, source, exception_head, message_head), items in grouped.items():
        group_times = [entry_time(item) for item in items if entry_time(item)]
        items.sort(key=lambda item: entry_time(item) or MIN_TS, reverse=True)
        clusters.append({
            "level": level,
            "count": len(items),
            "source": source,
            "exception_type": exception_head or None,
            "message_pattern": message_head,
            "first_seen_cst": fmt_cst(min(group_times)) if group_times else None,
            "last_seen_cst": fmt_cst(max(group_times)) if group_times else None,
            "share_of_window": round(len(items) / len(windowed), 4) if windowed else 0.0,
            "samples": [make_sample(item) for item in items[:2]],
        })
    clusters.sort(key=lambda c: (-LEVEL_ORDER.get(c["level"], 0), -c["count"], c["last_seen_cst"] or ""))
    clusters = clusters[: args.max_clusters]

    latest_problems = [
        {
            "time_cst": fmt_cst(entry_time(item)),
            "level": field(item, "level", "Level", default="Unknown"),
            "source": field(item, "source", "Source", default="(unknown)"),
            **make_sample(item),
        }
        for item in windowed
        if field(item, "level", "Level") in ("Error", "Fatal")
    ][:8]

    result = {
        "generated_at_utc": now.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "generated_at_cst": fmt_cst(now),
        "base_url": args.base_url,
        "query": {"level": args.level, "hours": args.hours, "search": args.search},
        "coverage": {
            "total_reported": total_reported,
            "pages_fetched": pages_to_fetch,
            "fetched_unique": len(entries),
            "after_window_filter": len(windowed),
            "possibly_truncated": len(entries) < total_reported or total_reported >= 2000,
            "server_capacity": 2000,
            "note": "服务端内存最多保留 2000 条（启动时还会从 logs/log-*.txt 回填）；"
                    "重启会丢失，触顶时窗口短于 --hours。多实例部署时只反映被命中的实例。",
        },
        "counts_by_level": dict(counts),
        "time_range_cst": {
            "earliest": fmt_cst(min(times)) if times else None,
            "latest": fmt_cst(max(times)) if times else None,
        },
        "top_sources": top_sources,
        "timeline_cst": timeline,
        "clusters": clusters,
        "latest_error_samples": latest_problems,
    }

    text = json.dumps(result, ensure_ascii=False, indent=2)
    if args.json_out:
        with open(args.json_out, "w", encoding="utf-8") as handle:
            handle.write(text)
        print(f"[ok] 已写入 {args.json_out}", file=sys.stderr)
    else:
        print(text)

    print(
        f"[summary] 服务端 total={total_reported}，拉取 {len(entries)} 条，窗口内 {len(windowed)} 条；"
        f"Fatal={counts.get('Fatal', 0)} Error={counts.get('Error', 0)} Warning={counts.get('Warning', 0)}；"
        f"聚类 {len(clusters)} 组"
        + ("；疑似被截断" if result["coverage"]["possibly_truncated"] else ""),
        file=sys.stderr,
    )


if __name__ == "__main__":
    main()
