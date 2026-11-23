mod database_backup;
mod dotnet_backup;
pub use database_backup::{check_library_backup, library_integrity, library_readable};
pub use dotnet_backup::prepare_library_restore;
pub mod capibara;
pub mod public_url;
mod scraped;
pub use scraped::catalog_metadata_only;
mod catalog;
mod delta;
mod filters;
mod profiles;
mod reconcile;
mod reconcile_catalog;
mod store;
mod user_state;
mod watch_party;
pub use watch_party::{PartyCommand, PartyContent};

pub use catalog::*;
pub use delta::*;
pub use profiles::*;
pub use store::*;
pub use user_state::*;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("El documento supera el límite de {0} bytes.")]
    DocumentTooLarge(usize),
    #[error("{0}")]
    Invalid(String),
    #[error("SQLite: {0}")]
    Database(#[from] rusqlite::Error),
    #[error("JSON: {0}")]
    Json(#[from] serde_json::Error),
    #[error("Storage lock unavailable")]
    Lock,
}

pub type Result<T> = std::result::Result<T, Error>;
