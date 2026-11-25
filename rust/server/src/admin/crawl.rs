use super::source::{Config, Failure, Preview, Source, PROVIDERS};
use futures_util::{stream::FuturesUnordered, StreamExt};
use rusqlite::{params, Connection, OptionalExtension};
use std::{
    fs::File,
    path::{Path, PathBuf},
    sync::{atomic::AtomicU64, Arc, Mutex, MutexGuard},
    time::Duration,
};
use tokio::time::timeout;
use umd_core::CatalogStore;
use uuid::Uuid;
pub(super) type ErrBox = Box<dyn std::error::Error + Send + Sync>;
type Res<T> = Result<T, ErrBox>;
pub(super) fn now() -> i64 {
    chrono::Utc::now().timestamp()
}
pub(super) fn date(stamp: Option<i64>) -> Option<String> {
    stamp
        .and_then(|s| chrono::DateTime::from_timestamp(s, 0))
        .map(|d| d.to_rfc3339())
}
#[derive(Clone)]
pub struct Admin(pub(super) Arc<Inner>);
pub(super) struct Inner {
    pub db: Mutex<Connection>,
    pub directory: PathBuf,
    pub config: Config,
    pub started: i64,
    pub requests: AtomicU64,
    pub failed: AtomicU64,
    pub manual: Mutex<Option<tokio::task::JoinHandle<()>>>,
    stopping: tokio::sync::watch::Sender<bool>,
}
impl Admin {
    pub fn open(directory: impl AsRef<Path>) -> Res<Self> {
        let config = Config::read()?;
        Self::configured(directory.as_ref(), config)
    }
    pub(super) fn configured(directory: &Path, config: Config) -> Res<Self> {
        let mut db = Connection::open(directory.join("crawler.db"))?;
        db.busy_timeout(Duration::from_secs(10))?;
        db.execute_batch("PRAGMA journal_mode=WAL;
   CREATE TABLE IF NOT EXISTS providers(id TEXT PRIMARY KEY,interval INTEGER NOT NULL DEFAULT 2,cursor TEXT,complete INTEGER NOT NULL DEFAULT 0,discovery INTEGER,updates INTEGER,attempt INTEGER,success INTEGER,failures INTEGER NOT NULL DEFAULT 0,kind TEXT,retry INTEGER);
   CREATE TABLE IF NOT EXISTS titles(provider TEXT NOT NULL,external TEXT NOT NULL,preview TEXT NOT NULL,due INTEGER NOT NULL DEFAULT 0,success INTEGER,failures INTEGER NOT NULL DEFAULT 0,last_run TEXT,PRIMARY KEY(provider,external));
   CREATE INDEX IF NOT EXISTS title_due ON titles(provider,due,external);
   CREATE TABLE IF NOT EXISTS fingerprints(provider TEXT, digest BLOB, PRIMARY KEY(provider,digest));
   CREATE TABLE IF NOT EXISTS cursors(provider TEXT,cursor TEXT,PRIMARY KEY(provider,cursor));
   CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY,started INTEGER NOT NULL,finished INTEGER,status TEXT NOT NULL,updated INTEGER NOT NULL DEFAULT 0,failures INTEGER NOT NULL DEFAULT 0);
   CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT,time INTEGER NOT NULL,level TEXT NOT NULL,provider TEXT,run TEXT,stage TEXT NOT NULL,status TEXT NOT NULL,message TEXT NOT NULL,http_status INTEGER);
   CREATE INDEX IF NOT EXISTS crawler_event_time ON events(time);
   CREATE TRIGGER IF NOT EXISTS prune_events AFTER INSERT ON events BEGIN DELETE FROM events WHERE id<=NEW.id-10000; END;")?;
        let tx = db.transaction()?;
        for &(p, _) in PROVIDERS {
            tx.execute("INSERT OR IGNORE INTO providers(id) VALUES(?)", [p])?;
        }
        tx.commit()?;
        let admin = Self(Arc::new(Inner {
            db: Mutex::new(db),
            config,
            directory: directory.into(),
            started: now(),
            requests: 0.into(),
            failed: 0.into(),
            manual: Mutex::new(None),
            stopping: tokio::sync::watch::channel(false).0,
        }));
        if let Some(_lock) = admin.lock()? {
            admin.recover()?;
        }
        Ok(admin)
    }
    pub(super) fn db(&self) -> Res<MutexGuard<'_, Connection>> {
        self.0
            .db
            .lock()
            .map_err(|_| "Crawler database unavailable".into())
    }

    async fn storage<T: Send + 'static>(
        &self,
        task: impl FnOnce(Self) -> Res<T> + Send + 'static,
    ) -> Res<T> {
        let admin = self.clone();
        Ok(tokio::task::spawn_blocking(move || task(admin)).await??)
    }
    pub(super) fn lock(&self) -> Res<Option<File>> {
        let file = File::options()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(self.0.directory.join("crawler.lock"))?;
        match file.try_lock() {
            Ok(()) => Ok(Some(file)),
            Err(std::fs::TryLockError::WouldBlock) => Ok(None),
            Err(e) => Err(e.into()),
        }
    }
    pub(super) fn recover(&self) -> Res<()> {
        self.db()?.execute(
            "UPDATE runs SET status='interrupted',finished=? WHERE finished IS NULL",
            [now()],
        )?;
        Ok(())
    }
    pub(super) fn begin(&self) -> Res<Option<(File, String)>> {
        let Some(lock) = self.lock()? else {
            return Ok(None);
        };
        self.recover()?;
        let run = Uuid::new_v4().to_string();
        self.db()?.execute(
            "INSERT INTO runs(id,started,status) VALUES(?,?,'running')",
            params![run, now()],
        )?;
        self.event(None, &run, "run", "info", "started", "Comenzó el rastreo.")?;
        Ok(Some((lock, run)))
    }
    pub(super) fn event(
        &self,
        provider: Option<&str>,
        run: &str,
        stage: &str,
        level: &str,
        status: &str,
        message: &str,
    ) -> Res<()> {
        self.event_http(provider, run, stage, level, status, message, None)
    }
    #[allow(clippy::too_many_arguments)]
    fn event_http(
        &self,
        provider: Option<&str>,
        run: &str,
        stage: &str,
        level: &str,
        status: &str,
        message: &str,
        http_status: Option<u16>,
    ) -> Res<()> {
        let db = self.db()?;
        db.execute("DELETE FROM events WHERE time<?", [now() - 86400])?;
        db.execute("INSERT INTO events(time,level,provider,run,stage,status,message,http_status) VALUES(?,?,?,?,?,?,?,?)",params![now(),level,provider,run,stage,status,message,http_status])?;
        Ok(())
    }
    pub fn server_event(
        &self,
        stage: &str,
        level: &str,
        status: &str,
        message: &str,
        http_status: Option<u16>,
    ) -> Res<()> {
        let db = self.db()?;
        db.execute("DELETE FROM events WHERE time<?", [now() - 86400])?;
        db.execute(
            "INSERT INTO events(time,level,stage,status,message,http_status)
            VALUES(?,?,?,?,?,?)",
            params![now(), level, stage, status, message, http_status],
        )?;
        Ok(())
    }
    pub async fn shutdown_signal(&self) {
        #[cfg(unix)]
        {
            use tokio::signal::unix::{signal, SignalKind};
            if let Ok(mut term) = signal(SignalKind::terminate()) {
                tokio::select! {_=term.recv()=>(),_=tokio::signal::ctrl_c()=>()}
            } else {
                let _ = tokio::signal::ctrl_c().await;
            }
        }
        #[cfg(not(unix))]
        {
            let _ = tokio::signal::ctrl_c().await;
        }
        self.0.stopping.send_replace(true);
    }
    pub async fn finish_manual(&self) -> Res<()> {
        let task = self
            .0
            .manual
            .lock()
            .map_err(|_| "Crawler task unavailable")?
            .take();
        if let Some(task) = task {
            task.await?;
        }
        Ok(())
    }
    pub(super) async fn execute(
        &self,
        catalog: CatalogStore,
        run: &str,
        _lock: File,
        only: Option<String>,
        retry: bool,
    ) -> Res<String> {
        let mut workers = FuturesUnordered::new();
        for &(id, _) in PROVIDERS {
            if only.as_deref().is_none_or(|o| o == id) && self.0.config.availability(id).is_none() {
                workers.push(self.provider(catalog.clone(), id, run, retry));
            }
        }
        let work = async {
            let mut interrupted = false;
            while let Some(result) = workers.next().await {
                if let Err(e) = result {
                    interrupted = true;
                    tracing::error!(%e,"Crawler persistence failed");
                }
            }
            interrupted
        };
        let mut stop = self.0.stopping.subscribe();
        let status = tokio::select! {
          answer=timeout(self.0.config.budget,work)=>match answer{Err(_)=>"budget_exhausted",Ok(true)=>"failed",Ok(false)=>{
            if self.db()?.query_row("SELECT failures FROM runs WHERE id=?",[run],|r|r.get::<_,u32>(0))?>0{"partial"}else{"completed"}
          }},
          _=stop.wait_for(|stopping| *stopping)=>"cancelled",
        };
        drop(workers);
        self.db()?.execute(
            "UPDATE runs SET status=?,finished=? WHERE id=?",
            params![status, now(), run],
        )?;
        self.event(
            None,
            run,
            "run",
            if status == "completed" {
                "info"
            } else {
                "warning"
            },
            status,
            "Terminó el rastreo.",
        )?;
        Ok(status.into())
    }
    pub async fn scrape_once(&self, catalog: CatalogStore) -> Res<i32> {
        let Some((lock, run)) = self.begin()? else {
            return Ok(0);
        };
        let job = self.execute(catalog, &run, lock, None, false);
        tokio::pin!(job);
        let status = tokio::select! {
            result=&mut job=>result?,
            _=self.shutdown_signal()=>job.await?,
        };
        Ok(match status.as_str() {
            "completed" | "budget_exhausted" => 0,
            "cancelled" => 130,
            _ => 2,
        })
    }
    fn provider_error(&self, id: &str, run: &str, stage: &str, f: &Failure) -> Res<()> {
        let mut db = self.db()?;
        let tx = db.transaction()?;
        tx.execute("UPDATE providers SET failures=failures+1,kind=?1,retry=?2+CASE WHEN ?3>0 THEN ?3 ELSE min(86400,3600*(1<<min(failures,5))) END WHERE id=?4",params![f.kind,now(),f.retry,id])?;
        tx.execute("UPDATE runs SET failures=failures+1 WHERE id=?", [run])?;
        tx.commit()?;
        drop(db);
        self.event_http(
            Some(id),
            run,
            stage,
            "error",
            f.kind,
            "El proveedor no pudo completar la operación.",
            f.status,
        )
    }
    async fn provider(&self, catalog: CatalogStore, id: &str, run: &str, retry: bool) -> Res<()> {
        let (interval,mut cursor,complete,discovery,updates,kind,retry_at)=self.db()?.query_row("SELECT interval,cursor,complete,discovery,updates,kind,retry FROM providers WHERE id=?",[id],|r|Ok((r.get::<_,u32>(0)?,r.get::<_,Option<String>>(1)?,r.get::<_,bool>(2)?,r.get::<_,Option<i64>>(3)?,r.get::<_,Option<i64>>(4)?,r.get::<_,Option<String>>(5)?,r.get::<_,Option<i64>>(6)?)))?;
        if retry_at.is_some_and(|t| t > now()) && (!retry || kind.as_deref() == Some("throttled")) {
            return Ok(());
        }
        self.db()?.execute(
            "UPDATE providers SET attempt=? WHERE id=?",
            params![now(), id],
        )?;
        let mut source = match Source::open(id, &self.0.config, interval).await {
            Ok(s) => s,
            Err(f) => return self.provider_error(id, run, "provider", &f),
        };
        let mut next_request = tokio::time::Instant::now();
        let distance = Duration::from_secs(interval.into());
        let rescan = complete && discovery.unwrap_or(0) <= now() - 7 * 86400;
        if rescan {
            let mut db = self.db()?;
            let tx = db.transaction()?;
            tx.execute("DELETE FROM cursors WHERE provider=?", [id])?;
            tx.execute("DELETE FROM fingerprints WHERE provider=?", [id])?;
            tx.execute(
                "UPDATE providers SET complete=0,cursor=NULL WHERE id=?",
                [id],
            )?;
            tx.commit()?;
            cursor = None;
        }
        for stage in ["updates", "discovery"] {
            if stage == "updates" && (updates.unwrap_or(0) > now() - 86400 || id == "umd-json-api")
            {
                continue;
            }
            if stage == "discovery" && complete && !rescan {
                continue;
            }
            for _ in 0..self.0.config.pages {
                tokio::time::sleep_until(next_request).await;
                let op = if stage == "updates" {
                    "updates"
                } else {
                    "page"
                };
                let reply = match timeout(
                    self.0.config.operation,
                    source.command(
                        op,
                        if stage == "updates" {
                            None
                        } else {
                            cursor.as_deref()
                        },
                        None,
                    ),
                )
                .await
                .unwrap_or_else(|_| Err(Failure::new("timeout")))
                {
                    Ok(v) => v,
                    Err(e) => return self.provider_error(id, run, stage, &e),
                };
                next_request = tokio::time::Instant::now() + distance;
                let Some(items) = reply.items else {
                    return self.provider_error(
                        id,
                        run,
                        stage,
                        &Failure::new("parse_or_storage_error"),
                    );
                };
                let provider = id.to_owned();
                let next = reply.next.clone();
                let repeating = self.storage(move |admin| {
                    let id = provider.as_str();
                    let mut db = admin.db()?;
                    let tx = db.transaction()?;
                    let mut repeat = stage == "discovery"
                        && if let Some(next) = &next {
                            next.is_empty()
                                || next.len() > 8192
                                || tx.execute(
                                    "INSERT OR IGNORE INTO cursors VALUES(?,?)",
                                    params![id, next],
                                )? == 0
                        } else {
                            false
                        };
                    if !repeat && stage == "discovery" && !items.is_empty() {
                        use sha2::{Digest, Sha256};
                        let ids = items
                            .iter()
                            .map(|p| p.external_id.as_str())
                            .collect::<std::collections::BTreeSet<_>>();
                        let mut digest = Sha256::new();
                        for key in ids {
                            digest.update((key.len() as u64).to_be_bytes());
                            digest.update(key.as_bytes());
                        }
                        repeat = tx.execute(
                            "INSERT OR IGNORE INTO fingerprints VALUES(?,?)",
                            params![id, digest.finalize().as_slice()],
                        )? == 0;
                    }
                    if !repeat {
                        for p in items {
                            if p.external_id.is_empty() || p.external_id.len() > 8192 {
                                return Err("Invalid provider identifier".into());
                            }
                            tx.execute("INSERT INTO titles(provider,external,preview) VALUES(?1,?2,?3) ON CONFLICT(provider,external) DO UPDATE SET preview=excluded.preview,due=CASE WHEN ?4 AND titles.failures=0 THEN 0 ELSE titles.due END",params![id,p.external_id,serde_json::to_string(&p.preview)?,stage=="updates"])?;
                        }
                        if stage == "updates" {
                            tx.execute(
                                "UPDATE providers SET updates=? WHERE id=?",
                                params![now(), id],
                            )?;
                        } else {
                            tx.execute(
                                "UPDATE providers SET cursor=?,complete=?,discovery=? WHERE id=?",
                                params![next, next.is_none(), now(), id],
                            )?;
                        }
                        tx.commit()?;
                    }
                    Ok(repeat)
                }).await?;
                if repeating {
                    return self.provider_error(
                        id,
                        run,
                        stage,
                        &Failure::new("parse_or_storage_error"),
                    );
                }
                cursor = if stage == "discovery" {
                    reply.next
                } else {
                    cursor
                };
                if stage == "updates" || cursor.is_none() {
                    break;
                }
            }
        }
        let mut consecutive = 0;
        loop {
            let (provider, run_id) = (id.to_owned(), run.to_owned());
            let entry = self.storage(move |admin| {
                let job=admin.db()?.query_row("SELECT external,preview FROM titles WHERE provider=?1 AND (due<=?2 OR (?3 AND failures>0)) AND last_run IS NOT ?4 ORDER BY due,external LIMIT 1",params![provider,now(),retry,run_id],|r|Ok((r.get::<_,String>(0)?,r.get::<_,String>(1)?))).optional()?;
                job.map(|(external_id, raw)| Ok(Preview { external_id, preview: serde_json::from_str(&raw)? })).transpose()
            }).await?;
            let Some(entry) = entry else { break };
            let external = entry.external_id.clone();
            if id != "umd-json-api" {
                tokio::time::sleep_until(next_request).await;
            }
            let reply = timeout(
                self.0.config.detail,
                source.command("detail", None, Some(&entry)),
            )
            .await
            .unwrap_or_else(|_| Err(Failure::new("timeout")));
            next_request = tokio::time::Instant::now() + distance;
            let result = match reply {
                Ok(v) => match v.item {
                    // The merge parses and writes a whole title: keep it off the
                    // async workers that serve requests.
                    Some(item) => {
                        let catalog = catalog.clone();
                        let (provider, external) = (id.to_owned(), external.clone());
                        tokio::task::spawn_blocking(move || {
                            catalog.merge_scraped(&provider, &external, item)
                        })
                        .await
                        .ok()
                        .and_then(|merged| merged.ok())
                        .ok_or_else(|| Failure::new("parse_or_storage_error"))
                    }
                    None => Err(Failure::new("parse_or_storage_error")),
                },
                Err(e) => Err(e),
            };
            match result {
                Ok(changed) => {
                    consecutive = 0;
                    let (provider, run_id) = (id.to_owned(), run.to_owned());
                    self.storage(move |admin| {
                        let mut db = admin.db()?;
                        let tx = db.transaction()?;
                        tx.execute("UPDATE titles SET due=?,success=?,failures=0,last_run=? WHERE provider=? AND external=?",params![now()+7*86400,now(),run_id,provider,external])?;
                        tx.execute("UPDATE runs SET updated=updated+? WHERE id=?", params![changed, run_id])?;
                        tx.commit()?;
                        Ok(())
                    }).await?;
                }
                Err(e) => {
                    if matches!(
                        e.kind,
                        "throttled"
                            | "timeout"
                            | "network"
                            | "challenge_or_access_denied"
                            | "worker_unavailable"
                    ) {
                        return self.provider_error(id, run, "detail", &e);
                    }
                    let unsupported = e.kind == "unsupported";
                    consecutive = if unsupported { 0 } else { consecutive + 1 };
                    let (provider, run_id) = (id.to_owned(), run.to_owned());
                    self.storage(move |admin| {
                        let mut db = admin.db()?;
                        let tx = db.transaction()?;
                        tx.execute("UPDATE titles SET failures=CASE WHEN ?1 THEN 0 ELSE failures+1 END,due=?2+CASE WHEN ?1 THEN 604800 ELSE min(86400,3600*(1<<min(failures,5))) END,last_run=?3 WHERE provider=?4 AND external=?5",params![unsupported,now(),run_id,provider,external])?;
                        if !unsupported {
                            tx.execute("UPDATE runs SET failures=failures+1 WHERE id=?", [run_id])?;
                        }
                        tx.commit()?;
                        Ok(())
                    }).await?;
                    self.event_http(
                        Some(id),
                        run,
                        "detail",
                        if unsupported { "info" } else { "error" },
                        e.kind,
                        "Se pospuso la ficha del título.",
                        e.status,
                    )?;
                    if consecutive >= 3 {
                        return self.provider_error(id, run, "circuit", &e);
                    }
                }
            }
            tokio::task::yield_now().await;
        }
        self.db()?.execute(
            "UPDATE providers SET success=?,retry=NULL,kind=NULL,failures=0 WHERE id=?",
            params![now(), id],
        )?;
        self.event(
            Some(id),
            run,
            "provider",
            "info",
            "completed",
            "Finalizó el rastreo del proveedor.",
        )
    }
}
