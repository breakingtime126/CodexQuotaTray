// SPDX-License-Identifier: Apache-2.0
//! Read-only, network-only adaptation of the official Codex dotenv bootstrap.
//! Parsing never changes process state. Only the finished allowlist is exported.
mod iter;
mod parse;
#[cfg(test)]
mod tests;

use crate::auth::ExistingAuthSource;
use crate::error::{DotenvStatus, ReaderError as Error};
use std::collections::HashMap;
use std::io::Read;
use std::path::Path;
use zeroize::Zeroizing;

type Result<T> = std::result::Result<T, Error>;
const MAX_DOTENV_BYTES: usize = 256 * 1024;
const MAX_EXPANDED_VALUE_BYTES: usize = 32 * 1024;
const MAX_RECORDS: usize = 1024;
const MAX_TOTAL_EXPANSION_BYTES: usize = 1024 * 1024;

/// Intentionally not Debug/Serialize: proxy settings can contain credentials.
/// Home/auth/token/routing keys can be private expansion symbols, never exports.
pub struct NetworkEnvironment {
    assignments: HashMap<String, Zeroizing<String>>,
    dotenv_status: DotenvStatus,
}

impl NetworkEnvironment {
    /// Reports only whether the selected home had a valid dotenv file.
    pub fn dotenv_status(&self) -> DotenvStatus {
        self.dotenv_status
    }

    pub fn read(source: &ExistingAuthSource) -> Result<Self> {
        Self::read_home(source.codex_home())
    }

    fn read_home(home: &Path) -> Result<Self> {
        let path = home.join(".env");
        let _entry = match std::fs::symlink_metadata(&path) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(Self {
                    assignments: HashMap::new(),
                    dotenv_status: DotenvStatus::Absent,
                });
            }
            Err(_) => return Err(Error::NetworkConfiguration),
        };
        // A present dangling symlink is invalid configuration, not an absent file.
        // Follow valid symlinks just as the existing read-only auth source does.
        let metadata = std::fs::metadata(&path).map_err(|_| Error::NetworkConfiguration)?;
        if !metadata.is_file() || metadata.len() > MAX_DOTENV_BYTES as u64 {
            return Err(Error::NetworkConfiguration);
        }
        let file = std::fs::File::open(&path).map_err(|_| Error::NetworkConfiguration)?;
        let metadata = file.metadata().map_err(|_| Error::NetworkConfiguration)?;
        if !metadata.is_file() || metadata.len() > MAX_DOTENV_BYTES as u64 {
            return Err(Error::NetworkConfiguration);
        }
        let mut bytes = Zeroizing::new(Vec::new());
        file.take((MAX_DOTENV_BYTES + 1) as u64)
            .read_to_end(&mut bytes)
            .map_err(|_| Error::NetworkConfiguration)?;
        Self::parse(&bytes, cfg!(windows), &|key| std::env::var(key).ok())
    }

    fn parse(
        bytes: &[u8],
        windows_case: bool,
        inherited: &dyn Fn(&str) -> Option<String>,
    ) -> Result<Self> {
        if bytes.len() > MAX_DOTENV_BYTES || bytes.contains(&0) {
            return Err(Error::NetworkConfiguration);
        }
        let mut parser = iter::Iter::new(bytes);
        let mut overlay: HashMap<String, Zeroizing<String>> = HashMap::new();
        let mut assignments = HashMap::new();
        let mut records = 0usize;
        let mut expanded_bytes = 0usize;
        loop {
            let lookup = |key: &str| {
                overlay
                    .get(&environment_key(key, windows_case))
                    .map(|value| value.to_string())
                    .or_else(|| inherited(key))
            };
            let Some(item) = parser.next(&lookup) else {
                break;
            };
            let (key, value) = item?;
            records += 1;
            expanded_bytes = expanded_bytes
                .saturating_add(key.len())
                .saturating_add(value.len());
            if records > MAX_RECORDS
                || expanded_bytes > MAX_TOTAL_EXPANSION_BYTES
                || value.len() > MAX_EXPANDED_VALUE_BYTES
                || value.contains('\0')
            {
                return Err(Error::NetworkConfiguration);
            }
            // Official Codex ignores this prefix case-insensitively. Keep the
            // parser's case-sensitive symbol table, but never override an inherited
            // CODEX_ value even in the virtual environment used for expansion.
            if key.to_ascii_uppercase().starts_with("CODEX_") {
                continue;
            }
            let lookup_key = environment_key(&key, windows_case);
            if is_network_key(&key, windows_case) {
                assignments.insert(lookup_key.clone(), Zeroizing::new(value.clone()));
            }
            overlay.insert(lookup_key, Zeroizing::new(value));
        }
        Ok(Self {
            assignments,
            dotenv_status: DotenvStatus::Loaded,
        })
    }

    /// Apply the fully validated allowlist before constructing any HTTP client.
    ///
    /// # Safety
    /// The caller must ensure no other thread or signal handler can read or write
    /// the process environment. The binary joins its read-only preflight worker
    /// before calling this and starts the quota worker only after it returns.
    pub unsafe fn apply(self) {
        for (key, value) in self.assignments {
            // Defense in depth: this type cannot export an arbitrary variable.
            debug_assert!(is_network_key(&key, cfg!(windows)));
            std::env::set_var(key, value.as_str());
        }
    }
}

fn environment_key(key: &str, windows_case: bool) -> String {
    if windows_case {
        key.to_ascii_uppercase()
    } else {
        key.to_owned()
    }
}

fn is_network_key(key: &str, windows_case: bool) -> bool {
    if windows_case {
        matches!(
            key.to_ascii_uppercase().as_str(),
            "HTTP_PROXY"
                | "HTTPS_PROXY"
                | "ALL_PROXY"
                | "NO_PROXY"
                | "SSL_CERT_FILE"
                | "SSL_CERT_DIR"
        )
    } else {
        matches!(
            key,
            "HTTP_PROXY"
                | "HTTPS_PROXY"
                | "ALL_PROXY"
                | "NO_PROXY"
                | "http_proxy"
                | "https_proxy"
                | "all_proxy"
                | "no_proxy"
                | "SSL_CERT_FILE"
                | "SSL_CERT_DIR"
        )
    }
}

#[cfg(test)]
#[test]
fn dotenv_metadata_distinguishes_absent_loaded_and_failed_files() {
    let home = tempfile::tempdir().unwrap();
    assert_eq!(
        NetworkEnvironment::read_home(home.path())
            .unwrap()
            .dotenv_status(),
        DotenvStatus::Absent
    );
    for text in [
        "",
        "# empty valid fixture\n",
        "HTTPS_PROXY=http://synthetic-user:private-value@localhost:1234\n",
    ] {
        std::fs::write(home.path().join(".env"), text).unwrap();
        assert_eq!(
            NetworkEnvironment::read_home(home.path())
                .unwrap()
                .dotenv_status(),
            DotenvStatus::Loaded
        );
    }
    std::fs::write(home.path().join(".env"), "HTTPS_PROXY='private-value").unwrap();
    assert!(matches!(
        NetworkEnvironment::read_home(home.path()),
        Err(Error::NetworkConfiguration)
    ));
}
