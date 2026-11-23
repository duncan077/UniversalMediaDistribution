use crate::{normalize, CatalogFilters};
use rusqlite::{functions::FunctionFlags, types::Value, Connection};

fn genre(label: &str) -> String {
    let key = normalize(&label.replace(['-', '_', '/', '.'], " "));
    match key.as_str() {
        "accion" => "action",
        "aventura" | "aventuras" => "adventure",
        "comedia" => "comedy",
        "crimen" | "policiaco" => "crime",
        "documental" => "documentary",
        "dramatico" => "drama",
        "familia" | "familiar" => "family",
        "fantasia" => "fantasy",
        "historia" | "historico" => "historical",
        "terror" => "horror",
        "magia" => "magic",
        "artes marciales" => "martial arts",
        "mechas" => "mecha",
        "militar" | "belica" => "military",
        "musica" | "musical" => "music",
        "misterio" => "mystery",
        "psicologico" => "psychological",
        "reality show" | "telerrealidad" => "reality",
        "romantico" | "romantica" => "romance",
        "escolares" | "escolar" | "vida escolar" => "school",
        "sci fi" | "scifi" | "ciencia ficcion" => "science fiction",
        "shojo" => "shoujo",
        "shonen" => "shounen",
        "cosas de la vida" | "recuentos de la vida" | "vida cotidiana" => "slice of life",
        "deportes" | "deporte" => "sports",
        "sobrenatural" => "supernatural",
        "suspenso" => "suspense",
        "tragedia" => "tragedy",
        "guerra" => "war",
        "oeste" => "western",
        _ => return key,
    }
    .into()
}

fn status(label: &str) -> &'static str {
    let label = normalize(label);
    for (name, terms) in [
        ("cancelled", &["cancel"][..]),
        ("paused", &["hiatus", "paus", "suspend", "on hold"][..]),
        (
            "announced",
            &[
                "announc",
                "upcoming",
                "proxim",
                "not yet aired",
                "planned",
                "pilot",
            ][..],
        ),
        (
            "completed",
            &[
                "finish", "complet", "finaliz", "terminad", "concluid", "ended",
            ][..],
        ),
        (
            "ongoing",
            &[
                "ongoing",
                "airing",
                "returning",
                "emision",
                "publicacion",
                "curso",
                "publishing",
            ][..],
        ),
    ] {
        if terms.iter().any(|s| label.contains(s)) {
            return name;
        }
    }
    "unknown"
}

pub fn register(db: &Connection) -> rusqlite::Result<()> {
    let flags = FunctionFlags::SQLITE_UTF8 | FunctionFlags::SQLITE_DETERMINISTIC;
    db.create_scalar_function("umd_genre", 1, flags, |c| {
        Ok(genre(&c.get::<Option<String>>(0)?.unwrap_or_default()))
    })?;
    db.create_scalar_function("umd_label", 1, flags, |c| {
        Ok(c.get::<Option<String>>(0)?
            .unwrap_or_default()
            .trim()
            .to_uppercase())
    })?;
    db.create_scalar_function("umd_status", 1, flags, |c| {
        Ok(status(&c.get::<Option<String>>(0)?.unwrap_or_default()))
    })?;
    db.create_scalar_function("umd_adult_tag", 1, flags, |c| {
        Ok(c.get::<Option<String>>(0)?
            .is_some_and(|s| crate::is_adult_tag(&s)))
    })?;
    db.create_scalar_function("umd_adult_rating", 1, flags, |c| {
        Ok(c.get::<Option<String>>(0)?
            .is_some_and(|s| crate::is_adult_rating(&s)))
    })?;
    Ok(())
}

pub(crate) const DISTRIBUTOR_ROLES_SQL: &str = "('distributor','distribution','publisher','licensor','provider','distribuidor','distribuidora','editorial','licenciante','proveedor')";

pub fn apply(f: &CatalogFilters, sql: &mut String, args: &mut Vec<Value>) {
    if let Some(year) = f.year {
        sql.push_str(" AND CASE WHEN json_extract(s.summary,'$.releaseYear') BETWEEN 1800 AND 2200
            THEN json_extract(s.summary,'$.releaseYear') ELSE cast(substr(trim(json_extract(s.summary,'$.releaseDate')),1,4) AS INTEGER) END = ?");
        args.push(year.into());
    }
    for (term, func) in [(&f.genre, "umd_genre"), (&f.category, "umd_label")] {
        if let Some(term) = term.as_deref().map(str::trim).filter(|s| !s.is_empty()) {
            args.push(
                if func == "umd_genre" {
                    genre(term)
                } else {
                    term.to_uppercase()
                }
                .into(),
            );
            let i = args.len();
            sql.push_str(&format!(" AND (EXISTS(SELECT 1 FROM json_each(s.summary,'$.genres') WHERE value IS NOT NULL AND {func}(value)=?{i}) OR EXISTS(SELECT 1 FROM json_each(s.summary,'$.categories') WHERE value IS NOT NULL AND {func}(value)=?{i}))"));
        }
    }
    if let Some(provider) = f
        .provider
        .as_deref()
        .map(str::trim)
        .filter(|s| !s.is_empty())
    {
        args.push(provider.to_uppercase().into());
        let i = args.len();
        sql.push_str(&format!(
            " AND (EXISTS(SELECT 1 FROM json_each(s.summary,'$.contributors') AS person
                WHERE person.value IS NOT NULL
                AND (person.value->>'kind' IS NULL OR lower(trim(person.value->>'kind'))='organization')
                AND umd_label(person.value->>'name')=?{i}
                AND EXISTS(SELECT 1 FROM json_each(person.value,'$.credits') AS credit
                    WHERE credit.value IS NOT NULL AND lower(trim(credit.value->>'role')) IN {DISTRIBUTOR_ROLES_SQL}))
              OR EXISTS(SELECT 1 FROM json_each(s.summary,'$.onlineProviders') AS src
                WHERE src.key IS NOT NULL AND umd_label(src.key)=?{i}))"
        ));
    }
    const SUBTYPE: &str =
        "replace(replace(replace(replace(lower(s.summary->>'subtype'),'movie.',''),'book.',''),'-','_'),' ','_')";
    if let Some(group) = &f.subtype_group {
        sql.push_str(&format!(
            " AND c.media_type='movie' AND ({SUBTYPE} IN ('anime_movie','anime_series'))=?"
        ));
        args.push(i64::from(group.eq_ignore_ascii_case("anime")).into());
    }
    if let Some(s) = &f.status {
        sql.push_str(&format!(" AND {SUBTYPE} IN ('manga','series','anime_movie','anime_series','comic','light_novel','web_novel') AND umd_status(s.summary->>'publicationStatus')=?"));
        args.push(s.to_lowercase().into());
    }
}
