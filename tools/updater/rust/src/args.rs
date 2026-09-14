use std::path::PathBuf;

#[derive(Debug, Clone)]
pub struct Config {
    pub pid: u32,
    pub zip: PathBuf,
    pub target: PathBuf,
    pub launch: String,
    pub args: String,
    pub keeps: Vec<String>,
    pub keep_file: String,
    pub requires: Vec<String>,
    pub sha256: Option<String>,
    pub strip: usize,
    pub timeout: u64,
    pub write_retries: u32,
    pub write_delay_ms: u64,
    pub max_uncompressed: u64,
    pub delete_zip: bool,
    pub dry_run: bool,
    pub elevate: bool,
    pub silent: bool,
    pub log: Option<PathBuf>,
    pub elevated_worker: bool,
    pub gui: bool,
    pub gui_title: Option<String>,
}

fn next_string(parser: &mut lexopt::Parser) -> Result<String, String> {
    let os_str = parser.value().map_err(|e| e.to_string())?;
    os_str.into_string().map_err(|_| "Argument contains invalid UTF-8 characters".to_string())
}

impl Config {
    pub fn parse() -> Result<Config, String> {
        let mut pid: u32 = 0;
        let mut zip: Option<PathBuf> = None;
        let mut target: Option<PathBuf> = None;
        let mut launch: Option<String> = None;
        let mut args = String::new();
        let mut keeps = Vec::new();
        let mut keep_file = String::from(".updatekeep");
        let mut requires = Vec::new();
        let mut sha256: Option<String> = None;
        let mut strip: usize = 0;
        let mut timeout: u64 = 60;
        let mut write_retries: u32 = 20;
        let mut write_delay_ms: u64 = 500;
        let mut max_uncompressed: u64 = 4 * 1024 * 1024 * 1024; // 4 GB default
        let mut delete_zip = false;
        let mut dry_run = false;
        let mut elevate = false;
        let mut silent = false;
        let mut log: Option<PathBuf> = None;
        let mut elevated_worker = false;
        let mut gui = false;
        let mut gui_title: Option<String> = None;

        let mut parser = lexopt::Parser::from_env();
        while let Some(arg) = parser.next().map_err(|e| e.to_string())? {
            use lexopt::Arg::*;
            match arg {
                Long("pid") => {
                    let val = next_string(&mut parser)?;
                    pid = val.parse::<u32>().map_err(|_| format!("Invalid pid: {}", val))?;
                }
                Long("zip") => {
                    let val = next_string(&mut parser)?;
                    zip = Some(PathBuf::from(val));
                }
                Long("target") => {
                    let val = next_string(&mut parser)?;
                    target = Some(PathBuf::from(val));
                }
                Long("launch") => {
                    let val = next_string(&mut parser)?;
                    launch = Some(val);
                }
                Long("args") => {
                    let val = next_string(&mut parser)?;
                    args = val;
                }
                Long("keep") => {
                    let val = next_string(&mut parser)?;
                    keeps.push(val);
                }
                Long("keep-file") => {
                    let val = next_string(&mut parser)?;
                    keep_file = val;
                }
                Long("require") => {
                    let val = next_string(&mut parser)?;
                    requires.push(val);
                }
                Long("sha256") => {
                    let val = next_string(&mut parser)?;
                    sha256 = Some(val.to_ascii_lowercase());
                }
                Long("strip") => {
                    let val = next_string(&mut parser)?;
                    strip = val.parse::<usize>().map_err(|_| format!("Invalid strip: {}", val))?;
                }
                Long("timeout") => {
                    let val = next_string(&mut parser)?;
                    timeout = val.parse::<u64>().map_err(|_| format!("Invalid timeout: {}", val))?;
                }
                Long("write-retries") => {
                    let val = next_string(&mut parser)?;
                    write_retries = val.parse::<u32>().map_err(|_| format!("Invalid write-retries: {}", val))?;
                }
                Long("write-delay-ms") => {
                    let val = next_string(&mut parser)?;
                    write_delay_ms = val.parse::<u64>().map_err(|_| format!("Invalid write-delay-ms: {}", val))?;
                }
                Long("max-uncompressed") => {
                    let val = next_string(&mut parser)?;
                    max_uncompressed = val.parse::<u64>().map_err(|_| format!("Invalid max-uncompressed: {}", val))?;
                }
                Long("delete-zip") => {
                    delete_zip = true;
                }
                Long("dry-run") => {
                    dry_run = true;
                }
                Long("elevate") => {
                    elevate = true;
                }
                Long("silent") => {
                    silent = true;
                }
                Long("log") => {
                    let val = next_string(&mut parser)?;
                    log = Some(PathBuf::from(val));
                }
                Long("elevated-worker") => {
                    elevated_worker = true;
                }
                Long("gui") => {
                    gui = true;
                }
                Long("gui-title") => {
                    gui_title = Some(next_string(&mut parser)?);
                }
                Short('h') | Long("help") => {
                    return Err(format!("Usage: updater.exe --pid <PID> --zip <ZIP> --target <DIR> --launch <EXE> [options]"));
                }
                _ => return Err(format!("Unexpected argument: {:?}", arg)),
            }
        }

        let zip = zip.ok_or_else(|| "Missing required parameter: --zip".to_string())?;
        let target = target.ok_or_else(|| "Missing required parameter: --target".to_string())?;
        let launch = launch.ok_or_else(|| "Missing required parameter: --launch".to_string())?;

        Ok(Config {
            pid,
            zip,
            target,
            launch,
            args,
            keeps,
            keep_file,
            requires,
            sha256,
            strip,
            timeout,
            write_retries,
            write_delay_ms,
            max_uncompressed,
            delete_zip,
            dry_run,
            elevate,
            silent,
            log,
            elevated_worker,
            gui,
            gui_title,
        })
    }
}
