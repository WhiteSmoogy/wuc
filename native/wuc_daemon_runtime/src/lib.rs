use std::collections::{HashMap, VecDeque};
use std::env;
use std::ffi::{c_char, CStr, CString};
use std::fs;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, OnceLock};
use std::thread;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const MAX_LOG_ENTRIES: usize = 10_000;
const REQUEST_CACHE_TTL: Duration = Duration::from_secs(300);
const HTTP_WAIT_SLACK_MS: u64 = 5_000;

#[derive(Copy, Clone, Eq, PartialEq)]
enum State {
    Ready,
    Reloading,
}

struct Runtime {
    state: State,
    max_queue_size: usize,
    queue: VecDeque<u64>,
    in_flight: HashMap<u64, Arc<CommandEntry>>,
    request_index: HashMap<String, Arc<CommandEntry>>,
    next_command_id: u64,
    logs: VecDeque<LogEntry>,
    server: Option<ServerState>,
    metadata: Metadata,
    shutting_down: bool,
}

struct ServerState {
    port: u16,
}

#[derive(Default)]
struct Metadata {
    project_id: String,
    project_path: String,
    instance_id: String,
    process_boot_id: String,
    pid: u32,
    port: u16,
    started_at_utc: String,
    updated_at_utc: String,
}

struct LogEntry {
    timestamp_utc: String,
    timestamp: String,
    kind: String,
    message: String,
    stack_trace: String,
}

struct CommandEntry {
    command_id: u64,
    path: String,
    body: String,
    request_id: Option<String>,
    state: Mutex<CommandStatus>,
    ready: Condvar,
}

enum CommandStatus {
    Queued,
    Completed {
        response_json: String,
        completed_at: Instant,
    },
}

struct HttpRequest {
    method: String,
    path: String,
    query: String,
    body: String,
}

static RUNTIME: OnceLock<Mutex<Option<Runtime>>> = OnceLock::new();
static ID_COUNTER: AtomicU64 = AtomicU64::new(1);

fn runtime() -> &'static Mutex<Option<Runtime>> {
    RUNTIME.get_or_init(|| Mutex::new(None))
}

fn cstr_to_string(ptr: *const c_char) -> String {
    if ptr.is_null() {
        return String::new();
    }

    unsafe { CStr::from_ptr(ptr) }
        .to_str()
        .map(|s| s.to_string())
        .unwrap_or_default()
}

fn escape_json(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 8);
    for ch in s.chars() {
        match ch {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if c <= '\u{1f}' => {
                use std::fmt::Write;
                let _ = write!(out, "\\u{:04x}", c as u32);
            }
            c => out.push(c),
        }
    }
    out
}

fn json_string(value: &str) -> String {
    format!("\"{}\"", escape_json(value))
}

fn generate_hex_id() -> String {
    let tick = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let counter = ID_COUNTER.fetch_add(1, Ordering::Relaxed) as u128;
    format!("{:016x}{:016x}", tick as u64, counter as u64)
}

fn ensure_runtime(max_queue_size: usize) -> Runtime {
    Runtime {
        state: State::Ready,
        max_queue_size,
        queue: VecDeque::new(),
        in_flight: HashMap::new(),
        request_index: HashMap::new(),
        next_command_id: 1,
        logs: VecDeque::new(),
        server: None,
        metadata: Metadata::default(),
        shutting_down: false,
    }
}

fn cleanup_request_cache(rt: &mut Runtime) {
    let now = Instant::now();
    let expired: Vec<String> = rt
        .request_index
        .iter()
        .filter_map(|(key, entry)| {
            let state = entry.state.lock().ok()?;
            match &*state {
                CommandStatus::Completed { completed_at, .. }
                    if now.duration_since(*completed_at) > REQUEST_CACHE_TTL =>
                {
                    Some(key.clone())
                }
                _ => None,
            }
        })
        .collect();

    for key in expired {
        rt.request_index.remove(&key);
    }
}

fn registry_file_path(instance_id: &str) -> Option<String> {
    let home = env::var("USERPROFILE")
        .ok()
        .or_else(|| env::var("HOME").ok())?;
    Some(format!("{home}\\.wuc\\instances\\{instance_id}.json"))
}

fn write_registration_file(metadata: &Metadata) {
    let Some(path) = registry_file_path(&metadata.instance_id) else {
        return;
    };

    let dir = match std::path::Path::new(&path).parent() {
        Some(parent) => parent,
        None => return,
    };

    if fs::create_dir_all(dir).is_err() {
        return;
    }

    let json = format!(
        "{{\"projectId\":{},\"projectPath\":{},\"instanceId\":{},\"pid\":{},\"port\":{},\"startedAtUtc\":{},\"updatedAtUtc\":{}}}",
        json_string(&metadata.project_id),
        json_string(&metadata.project_path),
        json_string(&metadata.instance_id),
        metadata.pid,
        metadata.port,
        json_string(&metadata.started_at_utc),
        json_string(&metadata.updated_at_utc),
    );

    let _ = fs::write(path, json);
}

fn remove_registration_file(instance_id: &str) {
    let Some(path) = registry_file_path(instance_id) else {
        return;
    };
    let _ = fs::remove_file(path);
}

fn bind_listener(start_port: u16, end_port: u16) -> Option<(TcpListener, u16)> {
    for port in start_port..=end_port {
        if let Ok(listener) = TcpListener::bind(("127.0.0.1", port)) {
            return Some((listener, port));
        }
    }
    None
}

fn spawn_accept_loop(listener: TcpListener) {
    let _ = listener.set_nonblocking(true);
    thread::spawn(move || loop {
        let should_stop = {
            let guard = runtime().lock().expect("runtime mutex poisoned");
            guard.as_ref().map(|rt| rt.shutting_down).unwrap_or(true)
        };
        if should_stop {
            break;
        }

        match listener.accept() {
            Ok((stream, _)) => {
                thread::spawn(move || handle_connection(stream));
            }
            Err(err) if err.kind() == std::io::ErrorKind::WouldBlock => {
                thread::sleep(Duration::from_millis(25));
            }
            Err(_) => {
                thread::sleep(Duration::from_millis(50));
            }
        }
    });
}

fn find_header_end(buf: &[u8]) -> Option<usize> {
    buf.windows(4)
        .position(|window| window == b"\r\n\r\n")
        .map(|idx| idx + 4)
}

fn split_target(target: &str) -> (String, String) {
    if let Some((path, query)) = target.split_once('?') {
        (path.to_string(), query.to_string())
    } else {
        (target.to_string(), String::new())
    }
}

fn parse_content_length(headers: &str) -> usize {
    for line in headers.lines() {
        if let Some((name, value)) = line.split_once(':') {
            if name.eq_ignore_ascii_case("content-length") {
                return value.trim().parse::<usize>().unwrap_or(0);
            }
        }
    }
    0
}

fn read_http_request(stream: &mut TcpStream) -> Option<HttpRequest> {
    let _ = stream.set_read_timeout(Some(Duration::from_secs(5)));

    let mut bytes = Vec::new();
    let mut scratch = [0u8; 4096];
    let header_end = loop {
        let count = stream.read(&mut scratch).ok()?;
        if count == 0 {
            return None;
        }
        bytes.extend_from_slice(&scratch[..count]);
        if let Some(end) = find_header_end(&bytes) {
            break end;
        }
        if bytes.len() > 2 * 1024 * 1024 {
            return None;
        }
    };

    let header_text = String::from_utf8_lossy(&bytes[..header_end]).to_string();
    let mut header_lines = header_text.split("\r\n");
    let request_line = header_lines.next()?.trim();
    let mut parts = request_line.split_whitespace();
    let method = parts.next()?.to_string();
    let target = parts.next()?.to_string();
    let content_length = parse_content_length(&header_text);

    let mut body_bytes = bytes[header_end..].to_vec();
    while body_bytes.len() < content_length {
        let count = stream.read(&mut scratch).ok()?;
        if count == 0 {
            break;
        }
        body_bytes.extend_from_slice(&scratch[..count]);
    }
    body_bytes.truncate(content_length);

    let (path, query) = split_target(&target);
    Some(HttpRequest {
        method,
        path,
        query,
        body: String::from_utf8_lossy(&body_bytes).into_owned(),
    })
}

fn reason_phrase(status_code: u16) -> &'static str {
    match status_code {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Internal Server Error",
    }
}

fn write_http_response(mut stream: TcpStream, status_code: u16, body: &str) {
    let body_bytes = body.as_bytes();
    let response = format!(
        "HTTP/1.1 {} {}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
        status_code,
        reason_phrase(status_code),
        body_bytes.len(),
    );

    let _ = stream.write_all(response.as_bytes());
    let _ = stream.write_all(body_bytes);
    let _ = stream.flush();
}

fn parse_query_count(query: &str, default: usize) -> usize {
    for pair in query.split('&') {
        let Some((key, value)) = pair.split_once('=') else {
            continue;
        };
        if key == "count" {
            return value.parse::<usize>().unwrap_or(default);
        }
    }
    default
}

fn extract_json_string(body: &str, field: &str) -> Option<String> {
    let pattern = format!("\"{field}\"");
    let field_start = body.find(&pattern)?;
    let rest = &body[field_start + pattern.len()..];
    let colon = rest.find(':')?;
    let rest = rest[colon + 1..].trim_start();
    let mut chars = rest.chars();
    if chars.next()? != '"' {
        return None;
    }

    let mut escaped = false;
    let mut out = String::new();
    for ch in chars {
        if escaped {
            match ch {
                '"' => out.push('"'),
                '\\' => out.push('\\'),
                '/' => out.push('/'),
                'b' => out.push('\u{0008}'),
                'f' => out.push('\u{000c}'),
                'n' => out.push('\n'),
                'r' => out.push('\r'),
                't' => out.push('\t'),
                _ => out.push(ch),
            }
            escaped = false;
            continue;
        }

        match ch {
            '\\' => escaped = true,
            '"' => return Some(out),
            _ => out.push(ch),
        }
    }

    None
}

fn extract_json_u64(body: &str, field: &str) -> Option<u64> {
    let pattern = format!("\"{field}\"");
    let field_start = body.find(&pattern)?;
    let rest = &body[field_start + pattern.len()..];
    let colon = rest.find(':')?;
    let rest = rest[colon + 1..].trim_start();

    let mut digits = String::new();
    for ch in rest.chars() {
        if ch.is_ascii_digit() {
            digits.push(ch);
        } else if !digits.is_empty() {
            break;
        } else if !ch.is_whitespace() {
            return None;
        }
    }

    digits.parse::<u64>().ok()
}

fn create_command(rt: &mut Runtime, body: &str, request_id: Option<String>) -> Option<Arc<CommandEntry>> {
    if rt.queue.len() >= rt.max_queue_size {
        return None;
    }

    let command_id = rt.next_command_id;
    rt.next_command_id += 1;

    let entry = Arc::new(CommandEntry {
        command_id,
        path: "/execute".to_string(),
        body: body.to_string(),
        request_id: request_id.clone(),
        state: Mutex::new(CommandStatus::Queued),
        ready: Condvar::new(),
    });

    rt.queue.push_back(command_id);
    rt.in_flight.insert(command_id, Arc::clone(&entry));
    if let Some(id) = request_id {
        rt.request_index.insert(id, Arc::clone(&entry));
    }

    Some(entry)
}

fn get_or_enqueue_command(body: &str) -> Result<Arc<CommandEntry>, String> {
    let request_id = extract_json_string(body, "requestId").filter(|value| !value.trim().is_empty());
    let mut guard = runtime().lock().expect("runtime mutex poisoned");
    let Some(rt) = guard.as_mut() else {
        return Err("native runtime unavailable".to_string());
    };

    cleanup_request_cache(rt);

    if let Some(id) = request_id.as_ref() {
        if let Some(existing) = rt.request_index.get(id) {
            return Ok(Arc::clone(existing));
        }
    }

    create_command(rt, body, request_id).ok_or_else(|| "daemon queue is full".to_string())
}

fn wait_for_command(command: &Arc<CommandEntry>, timeout_ms: u64) -> Option<String> {
    let deadline = Instant::now() + Duration::from_millis(timeout_ms);
    let mut state = command.state.lock().expect("command mutex poisoned");

    loop {
        match &*state {
            CommandStatus::Completed { response_json, .. } => return Some(response_json.clone()),
            CommandStatus::Queued => {
                let now = Instant::now();
                if now >= deadline {
                    return None;
                }

                let timeout = deadline.saturating_duration_since(now);
                let (next_state, timeout_result) = command
                    .ready
                    .wait_timeout(state, timeout)
                    .expect("command wait poisoned");
                state = next_state;
                if timeout_result.timed_out() {
                    return match &*state {
                        CommandStatus::Completed { response_json, .. } => Some(response_json.clone()),
                        CommandStatus::Queued => None,
                    };
                }
            }
        }
    }
}

fn command_json(entry: &CommandEntry) -> String {
    format!(
        "{{\"commandId\":{},\"path\":{},\"body\":{}}}",
        entry.command_id,
        json_string(&entry.path),
        json_string(&entry.body),
    )
}

fn build_identity_json(metadata: &Metadata, state: State, queue_depth: usize) -> String {
    format!(
        "{{\"projectId\":{},\"projectPath\":{},\"instanceId\":{},\"processBootId\":{},\"pid\":{},\"port\":{},\"startedAtUtc\":{},\"updatedAtUtc\":{},\"status\":{},\"daemonState\":{},\"queueDepth\":{}}}",
        json_string(&metadata.project_id),
        json_string(&metadata.project_path),
        json_string(&metadata.instance_id),
        json_string(&metadata.process_boot_id),
        metadata.pid,
        metadata.port,
        json_string(&metadata.started_at_utc),
        json_string(&metadata.updated_at_utc),
        json_string(match state {
            State::Ready => "ready",
            State::Reloading => "reloading",
        }),
        json_string(match state {
            State::Ready => "ready",
            State::Reloading => "reloading",
        }),
        queue_depth,
    )
}

fn build_logs_json(logs: &VecDeque<LogEntry>, count: usize) -> String {
    let start = logs.len().saturating_sub(count);
    let mut body = String::from("[");
    let mut first = true;
    for entry in logs.iter().skip(start) {
        if !first {
            body.push(',');
        }
        first = false;
        body.push_str(&format!(
            "{{\"timestamp\":{},\"timestampUtc\":{},\"type\":{},\"message\":{},\"stackTrace\":{}}}",
            json_string(&entry.timestamp),
            json_string(&entry.timestamp_utc),
            json_string(&entry.kind),
            json_string(&entry.message),
            json_string(&entry.stack_trace),
        ));
    }
    body.push(']');
    body
}

fn handle_execute_http(body: &str) -> (u16, String) {
    let timeout_ms = extract_json_u64(body, "timeoutMs").unwrap_or(30_000) + HTTP_WAIT_SLACK_MS;
    let command = match get_or_enqueue_command(body) {
        Ok(command) => command,
        Err(error) => {
            return (
                503,
                format!(
                    "{{\"success\":false,\"error\":{},\"logs\":[],\"executionTimeMs\":0}}",
                    json_string(&error)
                ),
            )
        }
    };

    match wait_for_command(&command, timeout_ms) {
        Some(response_json) => (200, response_json),
        None => (
            504,
            format!(
                "{{\"success\":false,\"error\":\"execute request timed out while waiting for Unity main thread\",\"logs\":[],\"executionTimeMs\":0,\"requestId\":{}}}",
                match &command.request_id {
                    Some(id) => json_string(id),
                    None => "null".to_string(),
                }
            ),
        ),
    }
}

fn handle_clear_logs_before(body: &str) -> String {
    let before = extract_json_string(body, "before").unwrap_or_default();
    let mut guard = runtime().lock().expect("runtime mutex poisoned");
    let Some(rt) = guard.as_mut() else {
        return "{\"ok\":false,\"removedCount\":0}".to_string();
    };

    let mut removed_count = 0usize;
    rt.logs.retain(|entry| {
        let keep = entry.timestamp_utc >= before;
        if !keep {
            removed_count += 1;
        }
        keep
    });

    format!(
        "{{\"ok\":true,\"before\":{},\"removedCount\":{}}}",
        json_string(&before),
        removed_count,
    )
}

fn route_request(req: HttpRequest) -> (u16, String) {
    match (req.method.as_str(), req.path.as_str()) {
        ("GET", "/identity") => {
            let guard = runtime().lock().expect("runtime mutex poisoned");
            let Some(rt) = guard.as_ref() else {
                return (500, "{\"error\":\"runtime unavailable\"}".to_string());
            };
            (200, build_identity_json(&rt.metadata, rt.state, rt.queue.len()))
        }
        ("GET", "/health") => {
            let guard = runtime().lock().expect("runtime mutex poisoned");
            let Some(rt) = guard.as_ref() else {
                return (500, "{\"error\":\"runtime unavailable\"}".to_string());
            };
            (200, build_identity_json(&rt.metadata, rt.state, rt.queue.len()))
        }
        ("GET", "/logs") => {
            let count = parse_query_count(&req.query, 100);
            let guard = runtime().lock().expect("runtime mutex poisoned");
            let Some(rt) = guard.as_ref() else {
                return (500, "{\"error\":\"runtime unavailable\"}".to_string());
            };
            (200, build_logs_json(&rt.logs, count))
        }
        ("POST", "/logs/clear") => {
            let mut guard = runtime().lock().expect("runtime mutex poisoned");
            let Some(rt) = guard.as_mut() else {
                return (500, "{\"error\":\"runtime unavailable\"}".to_string());
            };
            let removed_count = rt.logs.len();
            rt.logs.clear();
            (
                200,
                format!("{{\"ok\":true,\"removedCount\":{removed_count}}}"),
            )
        }
        ("POST", "/logs/clear-before") => (200, handle_clear_logs_before(&req.body)),
        ("POST", "/execute") => handle_execute_http(&req.body),
        _ => (404, "{\"error\":\"Not found\"}".to_string()),
    }
}

fn handle_connection(mut stream: TcpStream) {
    let response = match read_http_request(&mut stream) {
        Some(req) => route_request(req),
        None => (400, "{\"error\":\"Invalid request\"}".to_string()),
    };
    write_http_response(stream, response.0, &response.1);
}

#[no_mangle]
pub extern "C" fn wuc_daemon_init(max_queue_size: i32) -> i32 {
    let max = if max_queue_size <= 0 { 256 } else { max_queue_size as usize };
    let mut guard = runtime().lock().expect("runtime mutex poisoned");

    if let Some(rt) = guard.as_mut() {
        rt.max_queue_size = max;
        rt.shutting_down = false;
        return 1;
    }

    *guard = Some(ensure_runtime(max));
    1
}

#[no_mangle]
pub extern "C" fn wuc_daemon_attach_managed(
    project_id: *const c_char,
    project_path: *const c_char,
    pid: i32,
    port_range_start: i32,
    port_range_end: i32,
    started_at_utc: *const c_char,
    updated_at_utc: *const c_char,
) -> i32 {
    let mut guard = runtime().lock().expect("runtime mutex poisoned");
    let Some(rt) = guard.as_mut() else {
        return 0;
    };

    rt.shutting_down = false;
    rt.metadata.project_id = cstr_to_string(project_id);
    rt.metadata.project_path = cstr_to_string(project_path);
    rt.metadata.pid = pid.max(0) as u32;
    if rt.metadata.instance_id.is_empty() {
        rt.metadata.instance_id = generate_hex_id();
    }
    if rt.metadata.process_boot_id.is_empty() {
        rt.metadata.process_boot_id = generate_hex_id();
    }

    let started = cstr_to_string(started_at_utc);
    let updated = cstr_to_string(updated_at_utc);
    if rt.metadata.started_at_utc.is_empty() {
        rt.metadata.started_at_utc = started;
    }
    rt.metadata.updated_at_utc = updated;

    if rt.server.is_none() {
        let start = port_range_start.max(1) as u16;
        let end = port_range_end.max(port_range_start).max(1) as u16;
        let Some((listener, port)) = bind_listener(start, end) else {
            return 0;
        };
        rt.metadata.port = port;
        rt.server = Some(ServerState { port });
        spawn_accept_loop(listener);
    } else if let Some(server) = rt.server.as_ref() {
        rt.metadata.port = server.port;
    }

    write_registration_file(&rt.metadata);
    rt.metadata.port as i32
}

#[no_mangle]
pub extern "C" fn wuc_daemon_shutdown() {
    let mut guard = runtime().lock().expect("runtime mutex poisoned");
    if let Some(rt) = guard.as_mut() {
        rt.shutting_down = true;
        remove_registration_file(&rt.metadata.instance_id);
        rt.queue.clear();
        rt.in_flight.clear();
        rt.request_index.clear();
    }
}

#[no_mangle]
pub extern "C" fn wuc_daemon_set_state(state: i32) {
    let mut guard = runtime().lock().expect("runtime mutex poisoned");
    if let Some(rt) = guard.as_mut() {
        rt.state = if state == 0 { State::Ready } else { State::Reloading };
    }
}

#[no_mangle]
pub extern "C" fn wuc_daemon_state() -> i32 {
    let guard = runtime().lock().expect("runtime mutex poisoned");
    match guard.as_ref().map(|rt| rt.state) {
        Some(State::Ready) => 0,
        _ => 1,
    }
}

#[no_mangle]
pub extern "C" fn wuc_daemon_queue_depth() -> i32 {
    let guard = runtime().lock().expect("runtime mutex poisoned");
    guard
        .as_ref()
        .map(|rt| rt.queue.len() as i32)
        .unwrap_or(0)
}

#[no_mangle]
pub extern "C" fn wuc_daemon_identity_json() -> *mut c_char {
    let guard = runtime().lock().expect("runtime mutex poisoned");
    let Some(rt) = guard.as_ref() else {
        return std::ptr::null_mut();
    };

    let json = build_identity_json(&rt.metadata, rt.state, rt.queue.len());
    CString::new(json).map(|s| s.into_raw()).unwrap_or(std::ptr::null_mut())
}

#[no_mangle]
pub extern "C" fn wuc_daemon_try_dequeue_command_json() -> *mut c_char {
    let entry = {
        let mut guard = runtime().lock().expect("runtime mutex poisoned");
        let Some(rt) = guard.as_mut() else {
            return std::ptr::null_mut();
        };

        if rt.state != State::Ready {
            return std::ptr::null_mut();
        }

        let Some(command_id) = rt.queue.pop_front() else {
            return std::ptr::null_mut();
        };

        rt.in_flight.get(&command_id).cloned()
    };

    let Some(entry) = entry else {
        return std::ptr::null_mut();
    };

    CString::new(command_json(&entry))
        .map(|s| s.into_raw())
        .unwrap_or(std::ptr::null_mut())
}

#[no_mangle]
pub extern "C" fn wuc_daemon_complete_command(
    command_id: u64,
    response_json: *const c_char,
) -> i32 {
    let entry = {
        let mut guard = runtime().lock().expect("runtime mutex poisoned");
        let Some(rt) = guard.as_mut() else {
            return 0;
        };
        rt.in_flight.remove(&command_id)
    };

    let Some(entry) = entry else {
        return 0;
    };

    let response = cstr_to_string(response_json);
    let mut state = entry.state.lock().expect("command mutex poisoned");
    *state = CommandStatus::Completed {
        response_json: response,
        completed_at: Instant::now(),
    };
    entry.ready.notify_all();
    1
}

#[no_mangle]
pub extern "C" fn wuc_daemon_record_log(
    timestamp_utc: *const c_char,
    timestamp: *const c_char,
    kind: *const c_char,
    message: *const c_char,
    stack_trace: *const c_char,
) {
    let mut guard = runtime().lock().expect("runtime mutex poisoned");
    let Some(rt) = guard.as_mut() else {
        return;
    };

    if rt.logs.len() >= MAX_LOG_ENTRIES {
        rt.logs.pop_front();
    }

    rt.logs.push_back(LogEntry {
        timestamp_utc: cstr_to_string(timestamp_utc),
        timestamp: cstr_to_string(timestamp),
        kind: cstr_to_string(kind),
        message: cstr_to_string(message),
        stack_trace: cstr_to_string(stack_trace),
    });
}

#[no_mangle]
pub extern "C" fn wuc_daemon_string_free(ptr: *mut c_char) {
    if ptr.is_null() {
        return;
    }
    unsafe {
        let _ = CString::from_raw(ptr);
    }
}
