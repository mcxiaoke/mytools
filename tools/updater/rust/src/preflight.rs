use std::fs::File;
use std::io::Read;
use std::path::Path;
use sha2::{Digest, Sha256};
use windows_sys::Win32::Storage::FileSystem::GetDiskFreeSpaceExW;

use crate::args::Config;
use crate::keep::KeepManager;
use crate::winutil::to_u16_vec;

#[derive(Debug)]
pub struct PlanItem {
    pub zip_index: usize,
    pub original_name: String,
    pub rel_path: String,
    pub is_dir: bool,
    pub uncompressed_size: u64,
    pub is_kept: bool,
    pub keep_reason: &'static str,
}

pub struct PreflightResult {
    pub items: Vec<PlanItem>,
    pub total_write_size: u64,
}

pub fn run_preflight(config: &Config, keep_mgr: &KeepManager) -> Result<PreflightResult, String> {
    // 1. Verify ZIP file existence
    if !config.zip.exists() {
        return Err(format!("Update package not found: {}", config.zip.display()));
    }

    // 2. SHA-256 verification (if requested)
    if let Some(ref expected_sha) = config.sha256 {
        let mut file = File::open(&config.zip)
            .map_err(|e| format!("Failed to open zip for SHA256 check: {}", e))?;
        let mut hasher = Sha256::new();
        let mut buffer = [0u8; 64 * 1024];
        loop {
            let n = file.read(&mut buffer)
                .map_err(|e| format!("Error reading zip for SHA256: {}", e))?;
            if n == 0 {
                break;
            }
            hasher.update(&buffer[..n]);
        }
        let digest = hasher.finalize();
        let hash: String = digest.iter().map(|b| format!("{:02x}", b)).collect();
        if hash != *expected_sha {
            return Err(format!("SHA-256 mismatch: expected {}, got {}", expected_sha, hash));
        }
    }

    // 3. Open ZIP archive
    let file = File::open(&config.zip)
        .map_err(|e| format!("Failed to open zip archive: {}", e))?;
    let mut archive = zip::ZipArchive::new(file)
        .map_err(|e| format!("Invalid zip archive: {}", e))?;

    let mut items = Vec::new();
    let mut total_write_size: u64 = 0;
    let mut found_stripped_paths = std::collections::HashSet::new();

    for i in 0..archive.len() {
        let entry = archive.by_index_raw(i)
            .map_err(|e| format!("Corrupt zip entry at index {}: {}", i, e))?;

        let raw_name = entry.name();
        let is_dir = entry.is_dir() || raw_name.ends_with('/') || raw_name.ends_with('\\');

        // Check compression method
        let method = entry.compression();
        if method != zip::CompressionMethod::Stored && method != zip::CompressionMethod::Deflated {
            return Err(format!("Unsupported compression method in {}: {:?}", raw_name, method));
        }

        // Apply --strip
        let stripped = strip_prefix_components(raw_name, config.strip);
        if stripped.is_empty() {
            // Root directory stripped away, ignore
            continue;
        }

        // Zip Slip validation & Internal reserved names
        validate_path_safety(&stripped, raw_name)?;

        // Zip bomb prevention: compression ratio check for large entries
        let comp_size = entry.compressed_size();
        let uncompressed_size = entry.size();
        if comp_size > 0 && uncompressed_size > 10 * 1024 * 1024 {
            let ratio = uncompressed_size / comp_size;
            if ratio > 2000 {
                return Err(format!("Zip bomb detected (ratio {}:1 exceeds 2000:1): {}", ratio, raw_name));
            }
        }

        // Check keep rules
        let (kept, reason) = keep_mgr.is_kept(&stripped);

        if !kept && !is_dir {
            total_write_size = total_write_size.saturating_add(uncompressed_size);
            if total_write_size > config.max_uncompressed {
                return Err(format!(
                    "Zip bomb detected: planned write size exceeds max allowed limit of {} bytes",
                    config.max_uncompressed
                ));
            }
        }

        found_stripped_paths.insert(stripped.replace('\\', "/").to_ascii_lowercase());

        items.push(PlanItem {
            zip_index: i,
            original_name: raw_name.to_string(),
            rel_path: stripped,
            is_dir,
            uncompressed_size,
            is_kept: kept,
            keep_reason: reason,
        });
    }

    // 4. Validate --require paths
    for req in &config.requires {
        let req_norm = req.replace('\\', "/").trim_start_matches('/').to_ascii_lowercase();
        if !found_stripped_paths.contains(&req_norm) {
            return Err(format!("Required file not found in update package: {}", req));
        }
    }

    // 5. Check disk space
    check_disk_space(&config.target, total_write_size)?;

    Ok(PreflightResult {
        items,
        total_write_size,
    })
}

fn strip_prefix_components(name: &str, strip: usize) -> String {
    let normalized = name.replace('\\', "/");
    let mut parts: Vec<&str> = normalized.split('/').filter(|p| !p.is_empty()).collect();
    if strip >= parts.len() {
        return String::new();
    }
    parts.drain(0..strip);
    parts.join("\\")
}

fn validate_path_safety(rel_path: &str, original_name: &str) -> Result<(), String> {
    let lower = rel_path.replace('\\', "/").to_ascii_lowercase();

    // 0. Internal reserved names forbidden (protects backup tree, lock, and temporary dirs)
    if lower.starts_with(".updater_tmp")
        || lower.starts_with(".updater_bak")
        || lower == ".updater.lock"
        || lower.starts_with(".updater/")
        || lower == ".updater"
    {
        return Err(format!("Internal reserved name violation: {}", original_name));
    }

    // 1. Colon forbidden (drive letter or NTFS alternate data stream)
    if rel_path.contains(':') {
        return Err(format!("Zip Slip detected (drive letter/stream): {}", original_name));
    }
    // 2. Absolute path forbidden
    if rel_path.starts_with('\\') || rel_path.starts_with('/') {
        return Err(format!("Zip Slip detected (absolute path): {}", original_name));
    }
    // 3. Parent directory traversal forbidden
    for seg in rel_path.split(&['\\', '/'][..]) {
        if seg == ".." {
            return Err(format!("Zip Slip detected (path traversal '..'): {}", original_name));
        }
    }
    // 4. Null bytes forbidden
    if rel_path.contains('\0') {
        return Err(format!("Zip Slip detected (null byte): {}", original_name));
    }

    Ok(())
}

fn check_disk_space(target: &Path, needed_bytes: u64) -> Result<(), String> {
    let target_w = to_u16_vec(target.as_os_str());
    let mut free_bytes_available: u64 = 0;
    let mut total_bytes: u64 = 0;
    let mut total_free_bytes: u64 = 0;

    let ok = unsafe {
        GetDiskFreeSpaceExW(
            target_w.as_ptr(),
            &mut free_bytes_available,
            &mut total_bytes,
            &mut total_free_bytes,
        )
    };

    if ok != 0 {
        // Need: needed_bytes + 10MB safety margin
        let required = needed_bytes.saturating_add(10 * 1024 * 1024);
        if free_bytes_available < required {
            return Err(format!(
                "Insufficient disk space: need {} MB, available {} MB",
                required / (1024 * 1024),
                free_bytes_available / (1024 * 1024)
            ));
        }
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_strip() {
        assert_eq!(strip_prefix_components("pkg-1.0/lib.dll", 1), "lib.dll");
        assert_eq!(strip_prefix_components("a/b/c.txt", 2), "c.txt");
        assert_eq!(strip_prefix_components("pkg-1.0/", 1), "");
    }

    #[test]
    fn test_zip_slip() {
        assert!(validate_path_safety("../evil.exe", "../evil.exe").is_err());
        assert!(validate_path_safety("a/../../evil.exe", "a/../../evil.exe").is_err());
        assert!(validate_path_safety("C:\\evil.exe", "C:\\evil.exe").is_err());
        assert!(validate_path_safety("sub\\good.dll", "sub\\good.dll").is_ok());
    }
}
