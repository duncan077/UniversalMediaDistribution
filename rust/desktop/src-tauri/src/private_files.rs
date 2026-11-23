use crate::CommandResult;
use serde::{de::DeserializeOwned, Serialize};
use std::{
    fs,
    io::{ErrorKind, Write},
    path::PathBuf,
};
use uuid::Uuid;
pub struct PrivateFiles(pub PathBuf);
impl PrivateFiles {
    fn file(&self, profile: Uuid, name: &str) -> PathBuf {
        self.0.join(format!("{profile}-{name}.json"))
    }
    pub fn read<T: DeserializeOwned>(&self, profile: Uuid, name: &str) -> CommandResult<Option<T>> {
        match fs::read(self.file(profile, name)) {
            Ok(bytes) => serde_json::from_slice(&bytes)
                .map(Some)
                .map_err(|_| "Configuración de perfil inválida.".into()),
            Err(e) if e.kind() == ErrorKind::NotFound => Ok(None),
            Err(e) => Err(e.to_string()),
        }
    }
    pub fn save(&self, profile: Uuid, name: &str, value: &impl Serialize) -> CommandResult<()> {
        fs::create_dir_all(&self.0).map_err(|e| e.to_string())?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(&self.0, fs::Permissions::from_mode(0o700))
                .map_err(|e| e.to_string())?;
        }
        let mut file = tempfile::NamedTempFile::new_in(&self.0).map_err(|e| e.to_string())?;
        serde_json::to_writer(&mut file, value).map_err(|e| e.to_string())?;
        file.flush()
            .and_then(|_| file.as_file().sync_all())
            .map_err(|e| e.to_string())?;
        file.persist(self.file(profile, name))
            .map_err(|e| e.to_string())?;
        Ok(())
    }
    pub fn remove(&self, profile: Uuid, name: &str) -> CommandResult<()> {
        match fs::remove_file(self.file(profile, name)) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == ErrorKind::NotFound => Ok(()),
            Err(e) => Err(e.to_string()),
        }
    }
}
