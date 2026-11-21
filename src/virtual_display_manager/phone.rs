use super::{amyuni_idd, windows, AMYUNI_IDD_DEVICE_STRING};
use hbb_common::{anyhow::anyhow, bail, log, tokio, ResultType};
use std::{
    os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
    sync::{
        atomic::{AtomicBool, Ordering},
        Mutex,
    },
    time::{Duration, Instant},
};
use winapi::um::{
    synchapi::{SetEvent, WaitForSingleObject},
    winnt::HANDLE,
};
use winreg::{enums::*, RegKey};

static IN_USE: AtomicBool = AtomicBool::new(false);
pub(super) static DRIVER_GATE: Mutex<()> = Mutex::new(());

pub(super) fn in_use() -> bool {
    IN_USE.load(Ordering::Acquire)
}
const MODES: &str =
    r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\usbmmIdd\Parameters\Monitors";

extern "C" {
    fn set_phone_display_mode(name: *const u16, width: u32, height: u32) -> i32;
    fn start_phone_workspace_picker(
        command: *const u16,
        event_name: *const u16,
        event: *mut HANDLE,
    ) -> HANDLE;
}

pub(crate) struct Workspace {
    name: String,
    plugged: bool,
    picker: Option<(OwnedHandle, OwnedHandle)>,
    mode_slots: Vec<(String, String)>,
    size: (u32, u32),
}

fn monitor_names() -> Vec<String> {
    windows::get_device_names(Some(AMYUNI_IDD_DEVICE_STRING))
        .into_iter()
        .map(|s| s.trim_end_matches('\0').to_owned())
        .collect()
}

pub(crate) async fn open(width: i32, height: i32) -> ResultType<Workspace> {
    if !super::is_virtual_display_supported() || !crate::platform::is_installed() {
        bail!("The phone workspace needs an installed Windows 10 (2004+) client");
    }
    let (width, height) = crate::phone_mode::fit(width, height)
        .ok_or_else(|| anyhow!("Unsupported phone screen size"))?;
    tokio::task::spawn_blocking(move || Workspace::create(width, height)).await?
}

impl Workspace {
    fn create(width: u32, height: u32) -> ResultType<Self> {
        let helper = std::env::current_exe()?.with_file_name("rustdesk-phone-workspace.exe");
        if !helper.is_file() {
            bail!("Phone workspace picker is missing");
        }
        {
            let _gate = DRIVER_GATE.lock().unwrap();
            if IN_USE.swap(true, Ordering::AcqRel) {
                bail!("Phone workspace already in use");
            }
        }
        let mut workspace = Self {
            name: String::new(),
            plugged: false,
            picker: None,
            mode_slots: Vec::new(),
            size: (width, height),
        };
        // Amyuni's eject command takes no device ID. Never adopt another virtual monitor.
        if crate::privacy_mode::is_in_privacy_mode() || !monitor_names().is_empty() {
            bail!("An Amyuni monitor is already active");
        }
        let mut asynchronous = false;
        amyuni_idd::check_install_driver(&mut asynchronous)?;
        let start = Instant::now();
        let modes = loop {
            match RegKey::predef(HKEY_LOCAL_MACHINE)
                .open_subkey_with_flags(MODES, KEY_READ | KEY_SET_VALUE | KEY_WOW64_64KEY)
            {
                Ok(key) => break key,
                Err(_) if asynchronous && start.elapsed() < Duration::from_secs(4) => {
                    std::thread::sleep(Duration::from_millis(100))
                }
                Err(err) => return Err(err.into()),
            }
        };
        register_mode(&modes, &mut workspace.mode_slots, width, height)?;
        if width != height {
            if let Err(err) = register_mode(&modes, &mut workspace.mode_slots, height, width) {
                log::warn!("Phone display rotation unavailable: {}", err);
            }
        }
        // Amyuni reads custom modes when the monitor attaches. Prepare phone-sized
        // layouts for apps with different minimum widths before that point.
        for (w, h) in crate::phone_mode::reading_modes(width, height) {
            if let Err(err) = register_mode(&modes, &mut workspace.mode_slots, w, h) {
                log::debug!("Optional phone reading mode unavailable: {}", err);
                break;
            }
        }

        amyuni_idd::set_monitor_attached(true, Some(Duration::from_secs(4)))?;
        workspace.plugged = true;
        let start = Instant::now();
        let wide = |s: &str| s.encode_utf16().chain(Some(0)).collect::<Vec<_>>();
        loop {
            let names = monitor_names();
            if let [name] = names.as_slice() {
                workspace.name = name.clone();
                if unsafe { set_phone_display_mode(wide(name).as_ptr(), width, height) } == 0
                    && scrap::Display::all().is_ok_and(|displays| {
                        displays.iter().any(|d| {
                            d.is_online()
                                && workspace.matches(&d.name())
                                && d.width() == width as usize
                                && d.height() == height as usize
                        })
                    })
                {
                    break;
                }
            }
            if start.elapsed() >= Duration::from_secs(4) {
                bail!("Phone display mode did not become active");
            }
            std::thread::sleep(Duration::from_millis(100));
        }

        let event_name = format!(r"Local\RustDeskPhone-{}", uuid::Uuid::new_v4());
        let command = wide(&format!(
            "\"{}\" {} {}",
            helper.display(),
            workspace.name,
            event_name
        ));
        let mut event = std::ptr::null_mut();
        unsafe {
            let process = start_phone_workspace_picker(
                command.as_ptr(),
                wide(&event_name).as_ptr(),
                &mut event,
            );
            if process.is_null() {
                return Err(std::io::Error::last_os_error().into());
            }
            workspace.picker = Some((
                OwnedHandle::from_raw_handle(event.cast()),
                OwnedHandle::from_raw_handle(process.cast()),
            ));
        }
        Ok(workspace)
    }

    pub(crate) fn matches(&self, name: &str) -> bool {
        self.name == name.trim_end_matches('\0')
    }

    pub(crate) fn picker_closed(&self) -> bool {
        self.picker.as_ref().is_some_and(|(_, process)| unsafe {
            WaitForSingleObject(process.as_raw_handle().cast(), 0) == 0
        })
    }

    pub(crate) async fn rotate(&mut self, landscape: bool) {
        let (width, height) = self.size;
        if (width > height) == landscape || width == height {
            return;
        }
        let name = self.name.clone();
        let result = tokio::task::spawn_blocking(move || {
            if !monitor_names().contains(&name) {
                return false;
            }
            let wide: Vec<_> = name.encode_utf16().chain(Some(0)).collect();
            unsafe { set_phone_display_mode(wide.as_ptr(), height, width) == 0 }
        }).await;
        if matches!(result, Ok(true)) {
            self.size = (height, width);
        } else {
            hbb_common::throttled_log!(Duration::from_secs(30), warn, "Cannot rotate the phone display");
        }
    }

    pub(crate) async fn close(self) {
        if let Err(err) = tokio::task::spawn_blocking(move || drop(self)).await {
            log::warn!("Phone workspace cleanup: {}", err);
        }
    }
}

impl Drop for Workspace {
    fn drop(&mut self) {
        if let Some((event, process)) = self.picker.take() {
            unsafe {
                SetEvent(event.as_raw_handle().cast());
                WaitForSingleObject(process.as_raw_handle().cast(), 1500);
            }
        }
        if self.plugged {
            let names = monitor_names();
            if names.len() <= 1
                && names
                    .first()
                    .map_or(true, |n| self.name.is_empty() || self.matches(n))
            {
                if let Err(err) =
                    amyuni_idd::set_monitor_attached(false, Some(Duration::from_secs(2)))
                {
                    log::warn!("Cannot remove phone display: {}", err);
                }
            }
        }
        for (slot, mode) in self.mode_slots.drain(..) {
            let released = RegKey::predef(HKEY_LOCAL_MACHINE)
                .open_subkey_with_flags(MODES, KEY_READ | KEY_SET_VALUE | KEY_WOW64_64KEY)
                .and_then(|key| {
                    if key.get_value::<String, _>(&slot)? == mode {
                        key.delete_value(slot)?;
                    }
                    Ok(())
                });
            if let Err(err) = released {
                log::warn!("Cannot release phone display mode: {}", err);
            }
        }
        IN_USE.store(false, Ordering::Release);
    }
}

fn register_mode(modes: &RegKey, owned: &mut Vec<(String, String)>, width: u32, height: u32) -> ResultType<()> {
    let mode = format!("{width},{height}");
    let slots = modes.enum_values().collect::<std::io::Result<Vec<_>>>()?;
    if slots.iter().any(|(k, _)| modes.get_value::<String, _>(k).ok().as_ref() == Some(&mode)) {
        return Ok(());
    }
    let slot = (0..10).map(|n| n.to_string())
        .find(|key| !slots.iter().any(|(k, _)| k == key))
        .ok_or_else(|| anyhow!("No available Amyuni custom resolution slot"))?;
    modes.set_value(&slot, &mode)?;
    owned.push((slot, mode));
    Ok(())
}
