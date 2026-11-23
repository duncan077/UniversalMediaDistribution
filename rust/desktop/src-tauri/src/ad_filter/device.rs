#[derive(Clone, Copy, Default)]
pub(super) struct Policy {
    pub constrained: bool,
    pub defer_downloads: bool,
}

#[cfg(windows)]
pub(super) fn current() -> Policy {
    use windows_sys::Win32::{
        NetworkManagement::IpHelper::GetNetworkConnectivityHint,
        Networking::WinSock::NL_NETWORK_CONNECTIVITY_HINT,
        System::{
            Power::{GetSystemPowerStatus, SYSTEM_POWER_STATUS},
            SystemInformation::{GlobalMemoryStatusEx, MEMORYSTATUSEX},
        },
    };
    unsafe {
        let mut mem: MEMORYSTATUSEX = std::mem::zeroed();
        mem.dwLength = std::mem::size_of_val(&mem) as u32;
        let constrained =
            GlobalMemoryStatusEx(&mut mem) != 0 && mem.ullTotalPhys <= 3 * 1024 * 1024 * 1024;
        let mut p: SYSTEM_POWER_STATUS = std::mem::zeroed();
        let battery = GetSystemPowerStatus(&mut p) != 0
            && (p.SystemStatusFlag == 1 || (p.ACLineStatus == 0 && p.BatteryLifePercent <= 20));
        let mut hint: NL_NETWORK_CONNECTIVITY_HINT = std::mem::zeroed();
        let metered = GetNetworkConnectivityHint(&mut hint) == 0
            && (matches!(hint.ConnectivityCost, 2 | 3)
                || hint.Roaming
                || hint.OverDataLimit
                || hint.ApproachingDataLimit);
        Policy {
            constrained,
            defer_downloads: battery || metered,
        }
    }
}
#[cfg(target_os = "linux")]
pub(super) fn current() -> Policy {
    use gtk::gio::{prelude::NetworkMonitorExt, NetworkMonitor};
    let memory = std::fs::read_to_string("/proc/meminfo").unwrap_or_default();
    let kb = memory
        .lines()
        .find(|l| l.starts_with("MemTotal:"))
        .and_then(|l| l.split_whitespace().nth(1)?.parse::<u64>().ok());
    let battery = std::fs::read_dir("/sys/class/power_supply")
        .into_iter()
        .flatten()
        .flatten()
        .any(|e| {
            let p = e.path();
            std::fs::read_to_string(p.join("status"))
                .unwrap_or_default()
                .trim()
                == "Discharging"
                && std::fs::read_to_string(p.join("capacity"))
                    .ok()
                    .and_then(|s| s.trim().parse::<u8>().ok())
                    .is_some_and(|n| n <= 20)
        });
    Policy {
        constrained: kb.is_some_and(|n| n <= 3 * 1024 * 1024),
        defer_downloads: battery || NetworkMonitor::default().is_network_metered(),
    }
}
#[cfg(target_os = "android")]
static CONDITIONS: std::sync::atomic::AtomicU8 = std::sync::atomic::AtomicU8::new(3);
#[cfg(target_os = "android")]
pub(super) fn current() -> Policy {
    let bits = CONDITIONS.load(std::sync::atomic::Ordering::Relaxed);
    Policy {
        constrained: bits & 1 != 0,
        defer_downloads: bits & 2 != 0,
    }
}
#[cfg(target_os = "android")]
#[no_mangle]
pub extern "system" fn Java_org_universalmediadistribution_rust_PlayerFilter_conditions(
    _: jni::JNIEnv,
    _: jni::objects::JClass,
    low: jni::sys::jboolean,
    defer: jni::sys::jboolean,
) {
    CONDITIONS.store(
        (low != 0) as u8 | (((defer != 0) as u8) << 1),
        std::sync::atomic::Ordering::Relaxed,
    );
}
#[cfg(not(any(target_os = "android", target_os = "linux", windows)))]
pub(super) fn current() -> Policy {
    Policy::default()
}
