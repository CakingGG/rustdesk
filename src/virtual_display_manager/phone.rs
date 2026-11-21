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
    mode_slot: Option<(String, String)>,
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
            mode_slot: None,
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
        let mode = format!("{width},{height}");
        let slots = modes.enum_values().collect::<std::io::Result<Vec<_>>>()?;
        if !slots
            .iter()
            .any(|(key, _)| modes.get_value::<String, _>(key).ok().as_ref() == Some(&mode))
        {
            let slot = (0..10)
                .map(|n| n.to_string())
                .find(|key| !slots.iter().any(|(k, _)| k == key))
                .ok_or_else(|| anyhow!("No available Amyuni custom resolution slot"))?;
            modes.set_value(&slot, &mode)?;
            workspace.mode_slot = Some((slot, mode));
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
        if let Some((slot, mode)) = self.mode_slot.take() {
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
