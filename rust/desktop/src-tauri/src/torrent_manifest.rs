use crate::{downloads::Downloads, work, CommandResult};
use librqbit::{api::TorrentIdOrHash, Api, Magnet};
use serde_json::{json, Value};
use std::{
    collections::{BTreeMap, HashSet},
    time::Duration,
};
use tauri::State;
use tokio::io::AsyncReadExt;
use umd_core::{CatalogDocument, CatalogItem, CatalogStore};

const LIMIT: usize = 4 * 1024 * 1024;

fn relative(raw: &str) -> Option<String> {
    let path = raw.trim().replace('\\', "/");
    (!path.is_empty()
        && !path.chars().any(char::is_control)
        && path
            .split('/')
            .all(|s| !s.is_empty() && !matches!(s, "." | "..") && !s.contains(':')))
    .then_some(path)
}
fn matches(path: &str, reference: &str) -> bool {
    path == reference
        || path
            .strip_suffix(reference)
            .is_some_and(|s| s.ends_with('/'))
}
struct Files {
    paths: Vec<String>,
    manifest: usize,
}
impl Files {
    fn new(names: impl Iterator<Item = String>) -> CommandResult<Self> {
        let mut paths = vec![];
        let mut seen = HashSet::new();
        let mut manifest = None;
        for name in names {
            let p = relative(&name).ok_or("El torrent contiene una ruta inválida.")?;
            if !seen.insert(p.to_ascii_lowercase()) {
                return Err("El torrent repite una ruta.".into());
            }
            if p.rsplit('/')
                .next()
                .is_some_and(|n| n.eq_ignore_ascii_case("info.json"))
                && manifest.replace(paths.len()).is_some()
            {
                return Err("El torrent contiene varios manifiestos info.json.".into());
            }
            paths.push(p);
        }
        Ok(Self {
            paths,
            manifest: manifest.ok_or("El torrent no contiene info.json.")?,
        })
    }
    fn file(&self, raw: &str) -> CommandResult<usize> {
        let reference = relative(raw).ok_or("La ficha contiene una ruta inválida.")?;
        let mut found = self
            .paths
            .iter()
            .enumerate()
            .filter(|(_, p)| matches(p, &reference));
        let (i, _) = found
            .next()
            .ok_or("El archivo indicado no está en este torrent.")?;
        if found.next().is_some() || i == self.manifest {
            return Err("La referencia del manifiesto es ambigua.".into());
        }
        Ok(i)
    }
    fn images(&self, dir: &str) -> CommandResult<Vec<usize>> {
        let dir = relative(dir).ok_or("Carpeta de imágenes inválida.")?;
        let mut ordered = BTreeMap::new();
        let mut parent = None;
        for (i, path) in self.paths.iter().enumerate() {
            let Some((folder, name)) = path.rsplit_once('/') else {
                continue;
            };
            if !matches(folder, &dir) {
                continue;
            }
            let Some((stem, ext)) = name.rsplit_once('.') else {
                continue;
            };
            if !matches!(
                ext.to_ascii_lowercase().as_str(),
                "jpg" | "jpeg" | "png" | "webp" | "bmp" | "gif" | "avif"
            ) {
                continue;
            }
            let number = stem
                .rsplit_once('_')
                .and_then(|(_, n)| n.parse::<usize>().ok())
                .ok_or("Una imagen no tiene número de página.")?;
            if parent.replace(folder).is_some_and(|v| v != folder)
                || ordered.insert(number, i).is_some()
            {
                return Err("La secuencia de imágenes es ambigua.".into());
            }
        }
        if ordered.is_empty() || ordered.keys().copied().ne(0..ordered.len()) {
            return Err("Las páginas deben numerarse desde cero, sin saltos.".into());
        }
        Ok(ordered.into_values().collect())
    }
}
fn bind_node(
    v: &mut Value,
    files: &Files,
    hash: &str,
    private: bool,
    count: &mut usize,
) -> CommandResult<()> {
    let obj = v.as_object_mut().ok_or("Nodo del catálogo inválido.")?;
    if let Some(r) = obj.get_mut("thumbnailRef").filter(|r| !r.is_null()) {
        let i = files.file(r.as_str().ok_or("Referencia de portada inválida.")?)?;
        *r = json!(files.paths[i]);
    }
    for child in ["units", "seasons", "contributors"] {
        for row in obj
            .get_mut(child)
            .and_then(Value::as_array_mut)
            .into_iter()
            .flatten()
        {
            bind_node(row, files, hash, private, count)?;
        }
    }
    for source in obj
        .get_mut("sources")
        .and_then(Value::as_array_mut)
        .into_iter()
        .flatten()
    {
        let torrent = &source["torrent"];
        if let Some(id) = torrent["infoHashV1"].as_str().filter(|v| !v.is_empty()) {
            if !id.eq_ignore_ascii_case(hash) {
                return Err("La fuente declara otro torrent.".into());
            }
        }
        if let Some(m) = torrent["magnetUri"].as_str().filter(|v| !v.is_empty()) {
            if Magnet::parse(m)
                .ok()
                .and_then(|m| m.as_id20())
                .is_none_or(|h| h.as_string() != hash)
            {
                return Err("El magnet no corresponde al torrent actual.".into());
            }
        }
        if !torrent["infoHashV2"].is_null() || !torrent["torrentMetadataBase64"].is_null() {
            return Err("El manifiesto contiene metadatos de torrent independientes.".into());
        }
        let declared_private = torrent["isPrivate"] == true;
        if declared_private && !private {
            return Err("La ficha requiere un torrent privado.".into());
        }
        if source["sourceLayout"]
            .as_str()
            .is_some_and(|s| s.eq_ignore_ascii_case("image_sequence"))
        {
            let pages = files.images(
                source["filePath"]
                    .as_str()
                    .ok_or("Falta la carpeta de imágenes.")?,
            )?;
            source["umdFileIndices"] = json!(pages);
        } else {
            let path = source["filePath"]
                .as_str()
                .map(|s| files.file(s))
                .transpose()?;
            let index = source["fileIndex"]
                .as_u64()
                .map(|n| usize::try_from(n).map_err(|_| "Índice de archivo inválido."))
                .transpose()?;
            let i = path
                .or(index)
                .filter(|i| *i < files.paths.len() && *i != files.manifest)
                .ok_or("La fuente debe indicar un archivo del torrent.")?;
            if index.is_some_and(|v| v != i) {
                return Err("La ruta y el índice corresponden a archivos distintos.".into());
            }
            source["fileIndex"] = json!(i);
            source["filePath"] = json!(files.paths[i]);
        }
        let suffix = if private { "&x.umd.private=1" } else { "" };
        source["torrent"] = json!({"infoHashV1":hash,"magnetUri":format!("magnet:?xt=urn:btih:{hash}{suffix}"),"isPrivate":private,"trackers":[]});
        *count += 1;
    }
    Ok(())
}
fn bind(bytes: &[u8], files: &Files, hash: &str, private: bool) -> CommandResult<CatalogDocument> {
    let mut doc = CatalogDocument::parse(bytes).map_err(|e| e.to_string())?;
    let mut count = 0;
    for item in &mut doc.items {
        if item.deleted || item.sync_id.is_some() {
            return Err(
                "El manifiesto debe contener títulos sin identidades de otra biblioteca.".into(),
            );
        }
        let mut value = serde_json::to_value(&*item).map_err(|e| e.to_string())?;
        bind_node(&mut value, files, hash, private, &mut count)?;
        let mut bound: CatalogItem = serde_json::from_value(value).map_err(|e| e.to_string())?;
        for key in ["onlineProviders", "syncVersion"] {
            bound.metadata.remove(key);
        }
        *item = bound;
    }
    if count == 0 {
        return Err("El manifiesto no contiene fuentes del torrent.".into());
    }
    doc.validate().map_err(|e| e.to_string())?;
    Ok(doc)
}
#[tauri::command]
pub async fn import_download_manifest(
    id: usize,
    downloads: State<'_, Downloads>,
    store: State<'_, CatalogStore>,
) -> CommandResult<usize> {
    let (session, id) = downloads.for_id(id).await?;
    let api = Api::new(session.clone(), None);
    let handle = session
        .get(TorrentIdOrHash::Id(id))
        .ok_or("Descarga inexistente.")?;
    let private = handle
        .with_metadata(|m| m.info.info().private)
        .map_err(|_| "Faltan los metadatos del torrent.")?;
    let details = api
        .api_torrent_details(TorrentIdOrHash::Id(id))
        .map_err(|_| "No se pudo leer la lista de archivos.")?;
    let rows = details.files.ok_or("Faltan los metadatos del torrent.")?;
    let files = Files::new(rows.iter().map(|f| f.components.join("/")))?;
    if rows[files.manifest].length > LIMIT as u64 {
        return Err("info.json supera 4 MiB.".into());
    }
    let mut reader = api
        .api_stream(TorrentIdOrHash::Id(id), files.manifest)
        .await
        .map_err(|_| "No se pudo abrir info.json.")?
        .take(LIMIT as u64 + 1);
    let mut bytes = vec![];
    tokio::time::timeout(Duration::from_secs(90), reader.read_to_end(&mut bytes))
        .await
        .map_err(|_| "El manifiesto tardó demasiado.")?
        .map_err(|_| "No se pudo leer el manifiesto.")?;
    if bytes.len() as u64 != rows[files.manifest].length {
        return Err("El manifiesto está incompleto.".into());
    }
    let store = store.inner().clone();
    work(move || {
        store
            .import_local_sources(&bind(&bytes, &files, &details.info_hash, private)?)
            .map_err(|e| e.to_string())
    })
    .await
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn manifest_binds_a_complete_ordered_sequence_and_rejects_foreign_sources() {
        let paths = [
            "sub/info.json",
            "sub/chapters/1/page_1.png",
            "sub/chapters/1/page_0.png",
            "sub/movie.mp4",
        ];
        let files = Files::new(paths.into_iter().map(str::to_string)).unwrap();
        assert_eq!(files.images("chapters/1").unwrap(), vec![2, 1]);
        assert!(files.file("../clip").is_err());
        assert!(files.file("info.json").is_err());
        let mut row = json!({"units":[{"sources":[{"sourceLayout":"image_sequence","filePath":"chapters/1","torrent":{}}]}]});
        bind_node(&mut row, &files, "123", true, &mut 0).unwrap();
        assert_eq!(row["units"][0]["sources"][0]["torrent"]["isPrivate"], true);
        assert_eq!(
            row["units"][0]["sources"][0]["umdFileIndices"],
            json!([2, 1])
        );
        let mut bad = json!({"sources":[{"fileIndex":3,"torrent":{"infoHashV1":"other"}}]});
        assert!(bind_node(&mut bad, &files, "123", true, &mut 0).is_err());
    }
}
