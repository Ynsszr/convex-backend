//! Read deployment credentials without putting their contents in process
//! arguments.
use std::path::Path;

/// Read exactly one 256-bit hexadecimal secret, optionally followed by LF or
/// CRLF. Errors never include file contents. The open file, rather than a
/// preceding path check, owns the permissions/type check and bounded read.
pub(crate) fn read(path: &Path) -> anyhow::Result<String> {
    #[cfg(unix)]
    {
        use std::{
            fs::OpenOptions,
            io::Read,
            os::unix::fs::{
                MetadataExt,
                OpenOptionsExt,
                PermissionsExt,
            },
        };

        use anyhow::Context;

        // NOFOLLOW rejects a substituted final symlink. NONBLOCK prevents a
        // substituted FIFO/device from blocking before its type is checked.
        let file = OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK)
            .open(path)
            .context("opening instance secret file")?;
        let metadata = file.metadata().context("inspecting instance secret file")?;
        anyhow::ensure!(
            metadata.is_file(),
            "instance secret file must be a regular file"
        );
        anyhow::ensure!(
            metadata.permissions().mode() & 0o077 == 0,
            "instance secret file must not grant group or other permissions"
        );
        // SAFETY: geteuid takes no pointers and has no failure mode.
        anyhow::ensure!(
            metadata.uid() == unsafe { libc::geteuid() },
            "instance secret file must be owned by the current user"
        );
        anyhow::ensure!(
            (64..=66).contains(&metadata.len()),
            "instance secret file must contain one 64-character hexadecimal secret"
        );
        let mut bytes = Vec::new();
        file.take(67)
            .read_to_end(&mut bytes)
            .context("reading instance secret file")?;
        let secret = bytes
            .strip_suffix(b"\r\n")
            .or_else(|| bytes.strip_suffix(b"\n"))
            .unwrap_or(&bytes);
        anyhow::ensure!(
            secret.len() == 64 && secret.iter().all(u8::is_ascii_hexdigit),
            "instance secret file must contain one 64-character hexadecimal secret"
        );
        // ASCII hexadecimal was checked above; no decoder error can expose input.
        Ok(String::from_utf8(secret.to_vec()).expect("validated hexadecimal is UTF-8"))
    }
    #[cfg(not(unix))]
    {
        let _ = path;
        anyhow::bail!("instance secret files require Unix permission enforcement")
    }
}

#[cfg(all(test, unix))]
mod tests {
    use std::{
        fs,
        os::unix::fs::{
            symlink,
            PermissionsExt,
        },
    };

    use super::*;

    #[test]
    fn private_secret_accepts_only_one_hex_value_with_optional_newline() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("secret");
        let secret = "aB".repeat(32);
        for suffix in ["", "\n", "\r\n"] {
            fs::write(&path, format!("{secret}{suffix}")).unwrap();
            fs::set_permissions(&path, fs::Permissions::from_mode(0o600)).unwrap();
            assert_eq!(read(&path).unwrap(), secret);
        }
        for invalid in [
            "a".repeat(63),
            "g".repeat(64),
            format!("{secret}\n\n"),
            "a".repeat(4096),
        ] {
            fs::write(&path, &invalid).unwrap();
            let error = read(&path).unwrap_err().to_string();
            assert!(!error.contains(&invalid));
        }
    }

    #[test]
    fn secret_rejects_permissions_symlink_directory_and_fifo_without_reading() {
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("secret");
        fs::write(&path, "ab".repeat(32)).unwrap();
        for mode in [0o644, 0o640, 0o604, 0o601] {
            fs::set_permissions(&path, fs::Permissions::from_mode(mode)).unwrap();
            assert!(read(&path).is_err());
        }
        fs::set_permissions(&path, fs::Permissions::from_mode(0o400)).unwrap();
        assert!(read(&path).is_ok());
        let link = directory.path().join("link");
        symlink(&path, &link).unwrap();
        assert!(read(&link).is_err());
        assert!(read(directory.path()).is_err());
        let fifo = directory.path().join("fifo");
        let fifo_name = std::ffi::CString::new(fifo.as_os_str().as_encoded_bytes()).unwrap();
        // SAFETY: the CString lives through mkfifo and has a terminating NUL.
        assert_eq!(unsafe { libc::mkfifo(fifo_name.as_ptr(), 0o600) }, 0);
        assert!(read(&fifo).is_err());
    }
}
