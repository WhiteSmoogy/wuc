use once_cell::sync::Lazy;
use serde::Serialize;
use std::collections::VecDeque;
use std::ffi::{c_char, CStr, CString};
use std::sync::Mutex;

#[derive(Copy, Clone, Eq, PartialEq)]
enum State {
    Ready,
    Reloading,
}

#[derive(Serialize)]
struct QueuedCommand {
    path: String,
    body: String,
    #[serde(rename = "requestId")]
    request_id: String,
    #[serde(rename = "requestClass")]
    request_class: String,
}

struct Runtime {
    state: State,
    max_queue_size: usize,
    queue: VecDeque<QueuedCommand>,
}

static RUNTIME: Lazy<Mutex<Option<Runtime>>> = Lazy::new(|| Mutex::new(None));

fn cstr_to_string(ptr: *const c_char) -> String {
    if ptr.is_null() {
        return String::new();
    }
    unsafe { CStr::from_ptr(ptr) }
        .to_str()
        .map(|s| s.to_string())
        .unwrap_or_default()
}

#[no_mangle]
pub extern "C" fn wuc_daemon_init(max_queue_size: i32) -> i32 {
    let max = if max_queue_size <= 0 { 256 } else { max_queue_size as usize };
    let mut guard = RUNTIME.lock().expect("runtime mutex poisoned");
    *guard = Some(Runtime {
        state: State::Ready,
        max_queue_size: max,
        queue: VecDeque::new(),
    });
    1
}

#[no_mangle]
pub extern "C" fn wuc_daemon_shutdown() {
    let mut guard = RUNTIME.lock().expect("runtime mutex poisoned");
    *guard = None;
}

#[no_mangle]
pub extern "C" fn wuc_daemon_set_state(state: i32) {
    let mut guard = RUNTIME.lock().expect("runtime mutex poisoned");
    if let Some(runtime) = guard.as_mut() {
        runtime.state = if state == 0 { State::Ready } else { State::Reloading };
    }
}

#[no_mangle]
pub extern "C" fn wuc_daemon_state() -> i32 {
    let guard = RUNTIME.lock().expect("runtime mutex poisoned");
    match guard.as_ref().map(|r| r.state) {
        Some(State::Ready) => 0,
        _ => 1,
    }
}

#[no_mangle]
pub extern "C" fn wuc_daemon_queue_depth() -> i32 {
    let guard = RUNTIME.lock().expect("runtime mutex poisoned");
    guard
        .as_ref()
        .map(|r| r.queue.len() as i32)
        .unwrap_or(0)
}

#[no_mangle]
pub extern "C" fn wuc_daemon_enqueue(
    path: *const c_char,
    body: *const c_char,
    request_id: *const c_char,
    request_class: *const c_char,
) -> i32 {
    let mut guard = RUNTIME.lock().expect("runtime mutex poisoned");
    let Some(runtime) = guard.as_mut() else {
        return 0;
    };

    if runtime.queue.len() >= runtime.max_queue_size {
        return 0;
    }

    runtime.queue.push_back(QueuedCommand {
        path: cstr_to_string(path),
        body: cstr_to_string(body),
        request_id: cstr_to_string(request_id),
        request_class: cstr_to_string(request_class),
    });

    1
}

#[no_mangle]
pub extern "C" fn wuc_daemon_dequeue_json() -> *mut c_char {
    let mut guard = RUNTIME.lock().expect("runtime mutex poisoned");
    let Some(runtime) = guard.as_mut() else {
        return std::ptr::null_mut();
    };

    let Some(cmd) = runtime.queue.pop_front() else {
        return std::ptr::null_mut();
    };

    match serde_json::to_string(&cmd)
        .ok()
        .and_then(|s| CString::new(s).ok())
    {
        Some(s) => s.into_raw(),
        None => std::ptr::null_mut(),
    }
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
