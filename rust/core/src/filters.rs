use crate::{normalize, CatalogFilters};
use rusqlite::{functions::FunctionFlags, types::Value, Connection};

pub fn genre(label: &str) -> String {
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
    fn text<'a>(
        context: &'a rusqlite::functions::Context<'_>,
    ) -> rusqlite::Result<Option<&'a str>> {
        context
            .get_raw(0)
            .as_str_or_null()
            .map_err(|error| rusqlite::Error::UserFunctionError(Box::new(error)))
    }
    db.create_scalar_function("umd_genre", 1, flags, |c| {
        Ok(genre(text(c)?.unwrap_or_default()))
    })?;
    db.create_scalar_function("umd_label", 1, flags, |c| {
        Ok(text(c)?.unwrap_or_default().trim().to_uppercase())
    })?;
    db.create_scalar_function("umd_status", 1, flags, |c| {
        Ok(status(text(c)?.unwrap_or_default()))
    })?;
    db.create_scalar_function("umd_subtype_group", 3, flags, |c| {
        let media: String = c.get(0)?;
        let subtype: Option<String> = c.get(1)?;
        let group: String = c.get(2)?;
        Ok(subtype.is_some_and(|s| matches_subtype_group(&media, &s, &group)))
    })?;
    db.create_scalar_function("umd_adult_tag", 1, flags, |c| {
        Ok(text(c)?.is_some_and(crate::is_adult_tag))
    })?;
    db.create_scalar_function("umd_adult_rating", 1, flags, |c| {
        Ok(text(c)?.is_some_and(crate::is_adult_rating))
    })?;
    Ok(())
}

pub(crate) fn matches_subtype_group(media: &str, subtype: &str, group: &str) -> bool {
    let subtype = crate::user_state::canonical_subtype(media, subtype);
    match group.to_ascii_lowercase().as_str() {
        "anime" => {
            media.eq_ignore_ascii_case("movie")
                && matches!(subtype.as_str(), "anime_movie" | "anime_series")
        }
        "liveaction" => {
            media.eq_ignore_ascii_case("movie")
                && matches!(
                    subtype.as_str(),
                    "movie" | "series" | "documentary" | "short"
                )
        }
        "manga" => media.eq_ignore_ascii_case("book") && subtype == "manga",
        "comic" => media.eq_ignore_ascii_case("book") && subtype == "comic",
        "prose" => {
            media.eq_ignore_ascii_case("book")
                && matches!(
                    subtype.as_str(),
                    "book" | "novel" | "light_novel" | "web_novel"
                )
        }
        _ => false,
    }
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
        let media = if matches!(group.to_ascii_lowercase().as_str(), "anime" | "liveaction") {
            "movie"
        } else {
            "book"
        };
        sql.push_str(&format!(" AND c.media_type='{media}' AND umd_subtype_group(c.media_type,s.summary->>'subtype',?)"));
        args.push(group.to_ascii_lowercase().into());
    }
    if let Some(s) = &f.status {
        sql.push_str(&format!(" AND {SUBTYPE} IN ('manga','series','anime_movie','anime_series','comic','light_novel','web_novel') AND umd_status(s.summary->>'publicationStatus')=?"));
        args.push(s.to_lowercase().into());
    }
}
