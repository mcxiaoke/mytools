use std::fs::File;
use std::path::PathBuf;
use std::time::Duration;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let version = option_env!("MOCK_VERSION").unwrap_or("1.0.0");

    if args.contains(&"--version".to_string()) {
        println!("mock_host version {}", version);
        return;
    }

    // Check if launched after update
    if args.iter().any(|a| a.contains("--after-update")) {
        let current_dir = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."));
        let marker = current_dir.join("update_success.marker");
        let content = format!(
            "VERSION={}\nPID={}\nARGS={}\nCWD={}\n",
            version,
            std::process::id(),
            args.join(" "),
            current_dir.display()
        );
        let _ = std::fs::write(&marker, content);
        println!("mock_host: successfully relaunched after update (version {})", version);
        return;
    }

    if args.len() > 1 && args[1] == "lock-file" {
        // mock_host lock-file <file_path> <duration_secs>
        let file_path = &args[2];
        let duration: u64 = args.get(3).and_then(|s| s.parse().ok()).unwrap_or(5);

        println!("mock_host: holding exclusive lock on {} for {}s", file_path, duration);
        // Exclusive lock on Windows
        match open_exclusive(file_path) {
            Ok(_f) => {
                let _ = File::create(format!("{}.locked", file_path));
                std::thread::sleep(Duration::from_secs(duration));
                let _ = std::fs::remove_file(format!("{}.locked", file_path));
            }
            Err(e) => {
                eprintln!("mock_host: failed to lock file: {}", e);
                std::process::exit(1);
            }
        }
        return;
    }

    if args.len() > 1 && args[1] == "trigger-update" {
        // mock_host trigger-update <updater_exe> <zip_path> <target_dir>
        let updater_exe = &args[2];
        let zip_path = &args[3];
        let target_dir = &args[4];

        let my_pid = std::process::id();
        println!("mock_host: spawning updater from PID {}", my_pid);

        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            const DETACHED_PROCESS: u32 = 0x00000008;
            const CREATE_NEW_PROCESS_GROUP: u32 = 0x00000200;

            let status = std::process::Command::new(updater_exe)
                .args(&[
                    "--pid", &my_pid.to_string(),
                    "--zip", zip_path,
                    "--target", target_dir,
                    "--launch", "mock_host.exe",
                    "--args", "--after-update --tag verified",
                    "--delete-zip",
                    "--timeout", "30",
                    "--write-retries", "5",
                    "--write-delay-ms", "200",
                ])
                .creation_flags(DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP)
                .spawn();

            match status {
                Ok(_) => {
                    println!("mock_host: updater spawned successfully. Exiting self.");
                    std::process::exit(0);
                }
                Err(e) => {
                    eprintln!("mock_host: failed to spawn updater: {}", e);
                    std::process::exit(1);
                }
            }
        }
    }

    println!("mock_host running, PID: {}, version: {}", std::process::id(), version);
}

fn open_exclusive(path: &str) -> std::io::Result<File> {
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        use windows_sys::Win32::Storage::FileSystem::{
            CreateFileW, FILE_ATTRIBUTE_NORMAL, FILE_GENERIC_READ, FILE_GENERIC_WRITE,
            OPEN_ALWAYS,
        };
        use windows_sys::Win32::Foundation::INVALID_HANDLE_VALUE;
        use std::os::windows::io::{FromRawHandle, RawHandle};

        let path_w: Vec<u16> = std::ffi::OsStr::new(path)
            .encode_wide()
            .chain(Some(0))
            .collect();

        let handle = unsafe {
            CreateFileW(
                path_w.as_ptr(),
                FILE_GENERIC_READ | FILE_GENERIC_WRITE,
                0, // 0 = NO SHARING (Exclusive lock)
                std::ptr::null_mut(),
                OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                std::ptr::null_mut(),
            )
        };

        if handle == INVALID_HANDLE_VALUE {
            return Err(std::io::Error::last_os_error());
        }

        Ok(unsafe { File::from_raw_handle(handle as RawHandle) })
    }
    #[cfg(not(windows))]
    {
        File::open(path)
    }
}
