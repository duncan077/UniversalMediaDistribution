use crate::CommandResult;
use dom_query::{Document, NodeRef};
use serde::Deserialize;
use serde_json::{json, Value};
use std::{
    collections::HashMap,
    io::{Read, Seek},
    path::{Path, PathBuf},
};
use zip::ZipArchive;

#[derive(Deserialize)]
struct Container {
    rootfiles: RootFiles,
}
#[derive(Deserialize)]
struct RootFiles {
    rootfile: Vec<RootFile>,
}
#[derive(Deserialize)]
struct RootFile {
    #[serde(rename = "@full-path")]
    path: String,
}
#[derive(Deserialize)]
struct Package {
    manifest: Manifest,
    spine: Spine,
}
#[derive(Deserialize)]
struct Manifest {
    item: Vec<Item>,
}
#[derive(Deserialize)]
struct Item {
    #[serde(rename = "@id")]
    id: String,
    #[serde(rename = "@href")]
    href: String,
    #[serde(rename = "@properties", default)]
    properties: String,
    #[serde(rename = "@media-type", default)]
    mime: String,
}
#[derive(Deserialize)]
struct Spine {
    itemref: Vec<ItemRef>,
}
#[derive(Deserialize)]
struct ItemRef {
    #[serde(rename = "@idref")]
    id: String,
    #[serde(rename = "@linear")]
    linear: Option<String>,
}

fn local_path(base: &str, link: &str) -> Option<String> {
    let mut url = reqwest::Url::parse("https://book.invalid/").ok()?;
    url.set_path(base);
    let file = url.join(link).ok()?;
    if file.origin() != url.origin() || file.query().is_some() {
        return None;
    }
    let path = percent_encoding::percent_decode_str(file.path().strip_prefix('/')?)
        .decode_utf8()
        .ok()?;
    if path.split('/').any(|p| matches!(p, "" | "." | "..")) || path.contains(['\\', '\0']) {
        return None;
    }
    Some(path.into_owned())
}

pub(crate) trait ArchiveInput: Read + Seek + Send {}
impl<T: Read + Seek + Send> ArchiveInput for T {}
pub(crate) struct Book {
    zip: ZipArchive<Box<dyn ArchiveInput>>,
    spine: Vec<String>,
    pub(crate) titles: Vec<String>,
    remaining: usize,
    folder: PathBuf,
    images: HashMap<String, String>,
    image_paths: HashMap<String, String>,
}
impl Book {
    fn read(&mut self, name: &str, max: usize) -> CommandResult<Vec<u8>> {
        let file = self
            .zip
            .by_name(name)
            .map_err(|_| format!("Falta un recurso del EPUB: {name}"))?;
        let bound = max.min(self.remaining);
        if file.size() > bound as u64 {
            return Err("El EPUB supera el tamaño permitido.".into());
        }
        let mut bytes = Vec::new();
        file.take(bound as u64 + 1)
            .read_to_end(&mut bytes)
            .map_err(|e| e.to_string())?;
        if bytes.len() > bound {
            return Err("El EPUB supera el tamaño permitido.".into());
        }
        self.remaining -= bytes.len();
        Ok(bytes)
    }
    fn blocks(
        &mut self,
        node: NodeRef<'_>,
        base: &str,
        depth: usize,
        text: &mut String,
        out: &mut Vec<Value>,
    ) -> CommandResult<()> {
        if depth > 128 {
            return Err("El EPUB contiene demasiados niveles de elementos.".into());
        }
        if node.is_text() {
            text.push_str(&node.text());
            return Ok(());
        }
        let tag = node.node_name().unwrap_or_default();
        if matches!(
            tag.as_ref(),
            "script" | "style" | "iframe" | "object" | "template"
        ) {
            return Ok(());
        }
        let separate = matches!(
            tag.as_ref(),
            "p" | "div"
                | "h1"
                | "h2"
                | "h3"
                | "h4"
                | "h5"
                | "h6"
                | "li"
                | "section"
                | "blockquote"
                | "br"
                | "img"
                | "image"
        );
        if separate {
            flush(text, out);
        }
        if matches!(tag.as_ref(), "img" | "image") {
            if let Some(path) = node
                .attr("src")
                .or_else(|| node.attr("href"))
                .or_else(|| node.attr("xlink:href"))
                .and_then(|s| local_path(base, &s))
            {
                let ext = Path::new(&path)
                    .extension()
                    .and_then(|s| s.to_str())
                    .unwrap_or("")
                    .to_ascii_lowercase();
                if !matches!(
                    ext.as_str(),
                    "jpg" | "jpeg" | "png" | "webp" | "avif" | "gif"
                ) {
                    return Ok(());
                }
                let name = if let Some(name) = self.images.get(&path) {
                    name.clone()
                } else {
                    let name = format!("image-{}.{}", self.images.len(), ext);
                    self.image_paths.insert(name.clone(), path.clone());
                    self.images.insert(path, name.clone());
                    name
                };
                out.push(json!({"image":name,"alt":node.attr("alt").map(|s|s.to_string())}));
            }
        } else {
            let first = out.len();
            for child in node.children_it(false) {
                self.blocks(child, base, depth + 1, text, out)?;
            }
            if matches!(
                tag.as_ref(),
                "h1" | "h2" | "h3" | "h4" | "h5" | "h6" | "li" | "blockquote"
            ) {
                flush(text, out);
                for block in &mut out[first..] {
                    if block.get("text").is_some() && block.get("tag").is_none() {
                        block["tag"] = json!(tag.as_ref());
                    }
                }
            }
        }
        if separate {
            flush(text, out);
        }
        Ok(())
    }
}
fn flush(text: &mut String, out: &mut Vec<Value>) {
    if !text.trim().is_empty() {
        out.push(json!({"text":text.trim()}));
    }
    text.clear();
}

impl Book {
    pub(crate) fn image(&mut self, name: &str) -> CommandResult<Vec<u8>> {
        let path = self
            .image_paths
            .get(name)
            .cloned()
            .ok_or("Imagen EPUB desconocida.")?;
        let cache = self.folder.join(name);
        if cache.is_file() {
            return std::fs::read(cache).map_err(|e| e.to_string());
        }
        let data = self.read(&path, 16 << 20)?;
        std::fs::write(cache, &data).map_err(|e| e.to_string())?;
        Ok(data)
    }

    pub(crate) fn open(reader: Box<dyn ArchiveInput>, folder: PathBuf) -> CommandResult<Self> {
        let zip = ZipArchive::new(reader).map_err(|_| "EPUB inválido.")?;
        if zip.len() > 20_000 {
            return Err("El EPUB contiene demasiadas entradas.".into());
        }
        let mut book = Self {
            zip,
            folder,
            remaining: 256 << 20,
            images: HashMap::new(),
            image_paths: HashMap::new(),
            spine: Vec::new(),
            titles: Vec::new(),
        };
        let container: Container =
            quick_xml::de::from_reader(book.read("META-INF/container.xml", 256 * 1024)?.as_slice())
                .map_err(|_| "Contenedor EPUB inválido.")?;
        let package_path = container
            .rootfiles
            .rootfile
            .first()
            .and_then(|r| local_path("", &r.path))
            .ok_or("Falta el índice del EPUB.")?;
        let package: Package =
            quick_xml::de::from_reader(book.read(&package_path, 4 * 1024 * 1024)?.as_slice())
                .map_err(|_| "Índice EPUB inválido.")?;
        let mut labels = HashMap::new();
        if let Some((nav, ncx)) = package
            .manifest
            .item
            .iter()
            .filter_map(|item| {
                let nav = item.properties.split_whitespace().any(|p| p == "nav");
                let ncx = item.mime == "application/x-dtbncx+xml";
                (nav || ncx)
                    .then(|| local_path(&package_path, &item.href).map(|path| (path, ncx)))
                    .flatten()
            })
            .next()
        {
            if let Ok(data) = book.read(&nav, 4 << 20) {
                let doc = Document::from(String::from_utf8_lossy(&data).as_ref());
                if ncx {
                    for point in doc.select("navpoint").iter() {
                        let target = point
                            .select("content[src]")
                            .attr("src")
                            .and_then(|v| local_path(&nav, &v));
                        if let Some(path) = target {
                            labels.entry(path).or_insert_with(|| {
                                point
                                    .select("navlabel text")
                                    .first()
                                    .text()
                                    .trim()
                                    .to_owned()
                            });
                        }
                    }
                } else {
                    let navigation = doc
                        .select("nav")
                        .iter()
                        .find(|n| {
                            n.attr("epub:type")
                                .is_some_and(|s| s.split_whitespace().any(|s| s == "toc"))
                        })
                        .unwrap_or_else(|| doc.select("nav").first());
                    for link in navigation.select("a[href]").iter() {
                        if let Some(target) = link.attr("href").and_then(|v| local_path(&nav, &v)) {
                            labels
                                .entry(target)
                                .or_insert_with(|| link.text().trim().to_owned());
                        }
                    }
                }
            }
        }
        let manifest: HashMap<_, _> = package
            .manifest
            .item
            .into_iter()
            .map(|i| (i.id, i.href))
            .collect();
        for section in package
            .spine
            .itemref
            .into_iter()
            .filter(|s| s.linear.as_deref() != Some("no"))
        {
            let src = manifest
                .get(&section.id)
                .and_then(|s| local_path(&package_path, s))
                .ok_or("Capítulo EPUB inválido.")?;
            book.titles
                .push(labels.get(&src).cloned().unwrap_or_default());
            book.spine.push(src);
        }
        if book.spine.is_empty() || book.spine.len() > 10_000 {
            return Err("Índice de capítulos EPUB inválido.".into());
        }
        Ok(book)
    }
    pub(crate) fn section(&mut self, index: usize) -> CommandResult<Vec<u8>> {
        let file = self.folder.join(format!("section-{index}.json"));
        if file.is_file() {
            return std::fs::read(file).map_err(|e| e.to_string());
        }
        let src = self
            .spine
            .get(index)
            .ok_or("Capítulo inexistente.")?
            .clone();
        let bytes = self.read(&src, 4 * 1024 * 1024)?;
        let source =
            std::str::from_utf8(&bytes).map_err(|_| "El capítulo EPUB debe estar en UTF-8.")?;
        let doc = Document::from(source);
        let mut blocks = Vec::new();
        let mut text = String::new();
        self.blocks(
            doc.body().ok_or("Capítulo EPUB vacío.")?,
            &src,
            0,
            &mut text,
            &mut blocks,
        )?;
        flush(&mut text, &mut blocks);
        let title = ["h1", "h2", "title"]
            .iter()
            .find_map(|selector| {
                let text = doc.select(selector).first().text().trim().to_string();
                (!text.is_empty()).then_some(text)
            })
            .unwrap_or_default();
        if self.titles[index].is_empty() {
            self.titles[index] = title;
        }
        let data = serde_json::to_vec(&blocks).map_err(|e| e.to_string())?;
        std::fs::write(file, &data).map_err(|e| e.to_string())?;
        Ok(data)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use std::{fs::File, io::Write};

    #[test]
    fn reads_spine_order_inline_text_and_only_local_images() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("book.epub");
        let mut archive = zip::ZipWriter::new(File::create(&path).unwrap());
        for (name, content) in [
            (
                "META-INF/container.xml",
                r#"<container><rootfiles><rootfile full-path="book/title.opf"/></rootfiles></container>"#,
            ),
            (
                "book/title.opf",
                r#"<package><manifest><item id="b" href="2.xhtml"/><item id="a" href="1.xhtml"/></manifest><spine><itemref idref="a"/><itemref idref="b"/></spine></package>"#,
            ),
            (
                "book/1.xhtml",
                r#"<body><p>Hello <b>world</b></p><script>ignored</script><img src="img/a%20b.png"/><img src="https://outside.invalid/image.png"/></body>"#,
            ),
            ("book/2.xhtml", "<body><p>Final</p></body>"),
            ("book/img/a b.png", "abc"),
        ] {
            archive
                .start_file(name, zip::write::SimpleFileOptions::default())
                .unwrap();
            archive.write_all(content.as_bytes()).unwrap();
        }
        archive.finish().unwrap();
        let mut book =
            Book::open(Box::new(File::open(&path).unwrap()), dir.path().to_owned()).unwrap();
        assert_eq!(book.titles.len(), 2);
        let first: Value = serde_json::from_slice(&book.section(0).unwrap()).unwrap();
        assert_eq!(
            first,
            json!([{"text":"Hello world"},{"image":"image-0.png","alt":null}])
        );
        assert!(!dir.path().join("image-0.png").exists());
        assert_eq!(book.image("image-0.png").unwrap(), b"abc");
        let second: Value = serde_json::from_slice(&book.section(1).unwrap()).unwrap();
        assert_eq!(second, json!([{"text":"Final"}]));
    }
}
