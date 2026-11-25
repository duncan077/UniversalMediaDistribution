use serde_json::{json, Value};
use std::collections::{BTreeSet, HashMap};
use std::sync::Arc;

const SCRIPTLETS: &[&str] = &[
    "abort-current-script",
    "abort-on-property-read",
    "abort-on-property-write",
    "json-prune",
    "no-fetch-if",
    "no-xhr-if",
    "noeval-if",
    "nowebrtc",
    "prevent-setInterval",
    "prevent-setTimeout",
    "prevent-window-open",
    "remove-attr",
    "remove-class",
    "set-constant",
];
const LIBRARY: &str = include_str!("scriptlets.js");
const RUNTIME: &str = include_str!("runtime.js");
const SEED: &str = include_str!("../../player-assets/player-ad-slots.js");

#[derive(Default)]
pub(super) struct Rules {
    by_domain: HashMap<String, Vec<Arc<Rule>>>,
}
struct Rule {
    body: Box<str>,
    excluded: Box<[Box<str>]>,
    exception: bool,
    generic: bool,
}
fn matches(host: &str, domain: &str) -> bool {
    host == domain || host.strip_suffix(domain).is_some_and(|v| v.ends_with('.'))
}
impl Rules {
    pub fn add(&mut self, line: &str, constrained: bool) {
        let (domains, body, exception) = if let Some((d, b)) = line.split_once("#@#") {
            (d, b, true)
        } else if let Some((d, b)) = line.split_once("##") {
            (d, b, false)
        } else {
            return;
        };
        if line.len() > 4096 || domains.contains(['/', '#', '!']) || body.trim().is_empty() {
            return;
        }
        let mut include = Vec::new();
        let mut exclude = Vec::new();
        for d in domains.split(',').filter(|s| !s.is_empty()) {
            if let Some(v) = d.strip_prefix('~') {
                exclude.push(v.to_ascii_lowercase().into_boxed_str());
            } else {
                include.push(d.to_ascii_lowercase());
            }
        }
        let generic = include.is_empty();
        if generic && (constrained || body.starts_with("+js(")) {
            return;
        }
        if generic {
            include.push(String::new());
        }
        let rule = Arc::new(Rule {
            body: body.into(),
            excluded: exclude.into_boxed_slice(),
            exception,
            generic,
        });
        for d in include {
            self.by_domain.entry(d).or_default().push(rule.clone());
        }
    }
    fn config(&self, host: &str, constrained: bool) -> Value {
        let mut specific = BTreeSet::new();
        let mut generic = BTreeSet::new();
        let mut exceptions = BTreeSet::new();
        let mut domain = Some(host);
        while let Some(d) = domain {
            if let Some(rows) = self.by_domain.get(d) {
                for r in rows {
                    if r.excluded.iter().any(|e| matches(host, e)) {
                        continue;
                    }
                    if r.exception {
                        exceptions.insert(&r.body);
                    } else if r.generic {
                        generic.insert(&r.body);
                    } else {
                        specific.insert(&r.body);
                    }
                }
            }
            domain = if d.is_empty() {
                None
            } else {
                Some(d.split_once('.').map_or("", |(_, tail)| tail))
            };
        }
        let max = if constrained { 500 } else { 50000 };
        let mut selectors = Vec::new();
        let mut procedural = Vec::new();
        let mut scriptlets = Vec::new();
        for s in specific.difference(&exceptions).take(max) {
            if let Some(args) = s.strip_prefix("+js(").and_then(|v| v.strip_suffix(')')) {
                if let Some((name, args)) = invocation(args) {
                    scriptlets.push(json!({"name":name,"args":args}));
                }
            } else if procedure(s) {
                if !constrained {
                    procedural.push(s);
                }
            } else {
                selectors.push(s);
            }
        }
        let generic: Vec<_> = generic
            .difference(&exceptions)
            .filter(|s| !constrained && !procedure(s))
            .take(max)
            .collect();
        json!({"selectors":selectors,"genericSelectors":generic,"procedural":procedural,"scriptlets":scriptlets,
            "maxElementsPerPass":if constrained{500}else{2000},"proceduralBudgetMs":if constrained{6}else{12},
            "observerIdleMs":if constrained{3000}else{5000},"builtInDefenses":true})
    }
}
fn procedure(s: &str) -> bool {
    [
        ":has-text(",
        ":matches-css(",
        ":upward(",
        ":xpath(",
        ":remove(",
        ":min-text-length(",
    ]
    .iter()
    .any(|k| s.contains(k))
}
fn invocation(raw: &str) -> Option<(String, Vec<String>)> {
    let mut fields = Vec::new();
    let mut field = String::new();
    let mut chars = raw.chars().peekable();
    while let Some(c) = chars.next() {
        match c {
            '\\' if chars.peek() == Some(&',') => {
                chars.next();
                field.push(',');
            }
            ',' => {
                fields.push(field.trim().to_owned());
                field.clear();
            }
            _ => field.push(c),
        }
    }
    fields.push(field.trim().to_owned());
    let mut fields = fields.into_iter();
    let raw = fields.next()?;
    let name = match raw.trim_end_matches(".js") {
        "acs" => "abort-current-script",
        "aopr" => "abort-on-property-read",
        "aopw" => "abort-on-property-write",
        "set" => "set-constant",
        "nostif" => "prevent-setTimeout",
        "nosiif" => "prevent-setInterval",
        "nowoif" => "prevent-window-open",
        "ra" => "remove-attr",
        "rc" => "remove-class",
        v => v,
    };
    SCRIPTLETS
        .contains(&name)
        .then(|| (name.to_owned(), fields.collect()))
}
pub(super) fn script(host: &str, rules: Option<(&Rules, bool)>, enabled: bool) -> String {
    if !enabled {
        return String::new();
    }
    let Some((rules, constrained)) = rules else {
        return SEED.into();
    };
    let config = rules.config(host, constrained);
    let host = json!(host);
    format!("{SEED}\n;if(location.protocol==='https:' && location.hostname==={host}){{globalThis.__umdFilterConfig={config};\n{LIBRARY}\n{RUNTIME}\n}}")
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn resolves_domains_exceptions_and_bundled_scriptlets() {
        let mut rules = Rules::default();
        for s in [
            "##.banner",
            "example.org#@#.banner",
            "example.org,~member.example.org##.sponsor",
            "example.org##+js(rc, ad-class)",
            "example.org##+js(unregistered, payload)",
        ] {
            rules.add(s, false);
        }
        let config = rules.config("news.example.org", false);
        assert_eq!(config["selectors"], json!([".sponsor"]));
        assert_eq!(config["genericSelectors"], json!([]));
        assert_eq!(
            config["scriptlets"],
            json!([{"name":"remove-class","args":["ad-class"]}])
        );
        assert_eq!(
            rules.config("member.example.org", false)["selectors"],
            json!([])
        );
        assert_eq!(
            rules.config("another.org", false)["genericSelectors"],
            json!([".banner"])
        );
    }
}
