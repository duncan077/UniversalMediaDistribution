use crate::{normalize, CatalogStore, Error, Result};
use rusqlite::{params, Connection, TransactionBehavior};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

const ORIGINAL: &str = "00000000-0000-0000-0000-000000000001";

#[derive(Serialize)]
pub struct Profile {
    pub id: String,
    pub name: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Profiles {
    pub active_id: String,
    pub profiles: Vec<Profile>,
}

#[derive(Deserialize)]
#[serde(tag = "action", rename_all = "camelCase")]
pub enum ProfileEdit {
    Create { name: String },
    Rename { id: Uuid, name: String },
    Select { id: Uuid },
    Delete { id: Uuid },
}

pub(crate) fn initialize(db: &Connection) -> Result<Uuid> {
    let migrated: bool = db.query_row(
        "SELECT EXISTS(SELECT 1 FROM pragma_table_info('rust_favorites') WHERE name='profile_id')",
        [],
        |r| r.get(0),
    )?;
    if !migrated {
        crate::user_state::migrate_legacy_targets(db)?;
        db.execute_batch("CREATE TABLE rust_profiles(id TEXT PRIMARY KEY, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE, document TEXT);
            CREATE TABLE rust_profile_selection(singleton INTEGER PRIMARY KEY CHECK(singleton=1),profile_id TEXT NOT NULL REFERENCES rust_profiles);
            ALTER TABLE rust_favorites RENAME TO rust_single_favorites;
            CREATE TABLE rust_favorites(profile_id TEXT NOT NULL REFERENCES rust_profiles ON DELETE CASCADE,id TEXT NOT NULL REFERENCES rust_catalog ON DELETE CASCADE,PRIMARY KEY(profile_id,id));
            ALTER TABLE rust_backup_revisions RENAME TO rust_single_revisions;
            CREATE TABLE rust_backup_revisions(profile_id TEXT NOT NULL REFERENCES rust_profiles ON DELETE CASCADE,server TEXT NOT NULL,account TEXT NOT NULL,revision INTEGER NOT NULL,PRIMARY KEY(profile_id,server,account));")?;
        db.execute("INSERT INTO rust_profiles VALUES(?1,'Principal','principal',(SELECT value FROM rust_settings WHERE key='user_document'))",[ORIGINAL])?;
        db.execute(
            "INSERT INTO rust_profile_selection VALUES(1,?1)",
            [ORIGINAL],
        )?;
        db.execute(
            "INSERT INTO rust_favorites SELECT ?1,id FROM rust_single_favorites",
            [ORIGINAL],
        )?;
        db.execute("INSERT INTO rust_backup_revisions SELECT ?1,server,account,revision FROM rust_single_revisions",[ORIGINAL])?;
        db.execute_batch("DROP TABLE rust_single_favorites; DROP TABLE rust_single_revisions; DELETE FROM rust_settings WHERE key='user_document'")?;
    }
    let active: String = db.query_row(
        "SELECT profile_id FROM rust_profile_selection WHERE singleton=1",
        [],
        |r| r.get(0),
    )?;
    Uuid::parse_str(&active).map_err(|_| Error::Invalid("Identidad de perfil inválida.".into()))
}

fn profile_name(db: &Connection, name: &str, id: &str) -> Result<(String, String)> {
    let name = name.trim();
    let key = normalize(name);
    if key.is_empty() || name.chars().count() > 40 {
        return Err(Error::Invalid(
            "El nombre del perfil debe tener entre 1 y 40 caracteres.".into(),
        ));
    }
    let exists: bool = db.query_row(
        "SELECT EXISTS(SELECT 1 FROM rust_profiles WHERE name_key=?1 AND id!=?2)",
        params![key, id],
        |r| r.get(0),
    )?;
    if exists {
        return Err(Error::Invalid("Ya existe un perfil con ese nombre.".into()));
    }
    Ok((name.into(), key))
}

impl CatalogStore {
    pub fn for_profile(&self, id: Uuid) -> Result<Self> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let exists: bool = db.query_row(
            "SELECT EXISTS(SELECT 1 FROM rust_profiles WHERE id=?1)",
            [id.to_string()],
            |r| r.get(0),
        )?;
        if !exists {
            return Err(Error::Invalid("El perfil ya no existe.".into()));
        }
        Ok(Self(self.0.clone(), id, self.2.clone()))
    }

    pub fn profiles(&self) -> Result<Profiles> {
        let db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.unchecked_transaction()?;
        let active_id = tx.query_row(
            "SELECT profile_id FROM rust_profile_selection WHERE singleton=1",
            [],
            |r| r.get(0),
        )?;
        let mut stmt = tx.prepare("SELECT id,name FROM rust_profiles ORDER BY rowid")?;
        let profiles = stmt
            .query_map([], |r| {
                Ok(Profile {
                    id: r.get(0)?,
                    name: r.get(1)?,
                })
            })?
            .collect::<rusqlite::Result<_>>()?;
        Ok(Profiles {
            active_id,
            profiles,
        })
    }

    pub fn edit_profile(&self, edit: ProfileEdit) -> Result<()> {
        let mut db = self.0.lock().map_err(|_| Error::Lock)?;
        let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
        let changed = match edit {
            ProfileEdit::Create { name } => {
                let id = Uuid::new_v4().to_string();
                let (name, key) = profile_name(&tx, &name, &id)?;
                tx.execute(
                    "INSERT INTO rust_profiles(id,name,name_key) VALUES(?1,?2,?3)",
                    params![id, name, key],
                )?
            }
            ProfileEdit::Rename { id, name } => {
                let id = id.to_string();
                let (name, key) = profile_name(&tx, &name, &id)?;
                tx.execute(
                    "UPDATE rust_profiles SET name=?2,name_key=?3 WHERE id=?1",
                    params![id, name, key],
                )?
            }
            ProfileEdit::Select { id } => tx.execute(
                "UPDATE rust_profile_selection SET profile_id=?1
                 WHERE EXISTS(SELECT 1 FROM rust_profiles WHERE id=?1)",
                [id.to_string()],
            )?,
            ProfileEdit::Delete { id } => {
                let count: i64 =
                    tx.query_row("SELECT count(*) FROM rust_profiles", [], |r| r.get(0))?;
                if count <= 1 {
                    return Err(Error::Invalid("Debe quedar al menos un perfil.".into()));
                }
                let id = id.to_string();
                tx.execute(
                    "UPDATE rust_profile_selection SET profile_id=
                    (SELECT id FROM rust_profiles WHERE id!=?1 ORDER BY rowid LIMIT 1)
                    WHERE profile_id=?1",
                    [&id],
                )?;
                tx.execute("DELETE FROM rust_profiles WHERE id=?1", [id])?
            }
        };
        if changed == 0 {
            return Err(Error::Invalid("El perfil ya no existe.".into()));
        }
        tx.commit()?;
        Ok(())
    }
}
