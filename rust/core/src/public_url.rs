use std::net::IpAddr;
use url::Url;

pub fn public_ip(ip: IpAddr) -> bool {
    match ip {
        IpAddr::V4(v) => {
            let [a, b, c, _] = v.octets();
            !(v.is_private()
                || v.is_loopback()
                || v.is_link_local()
                || v.is_documentation()
                || a == 0
                || a >= 224
                || (a == 100 && (64..128).contains(&b))
                || (a == 192 && ((b == 0 && c == 0) || (b == 88 && c == 99)))
                || (a == 198 && (18..=19).contains(&b)))
        }
        IpAddr::V6(v) => {
            let s = v.segments();
            s[0] & 0xe000 == 0x2000
                && s[0] != 0x2002
                && !(s[0] == 0x2001 && (s[1] < 0x200 || s[1] == 0xdb8))
                && !(s[0] == 0x3fff && s[1] < 0x1000)
        }
    }
}

const BLOCKED_PORTS: &[u16] = &[
    22, 23, 25, 110, 143, 445, 465, 587, 993, 995, 1433, 3306, 5432, 6379, 11211, 27017,
];

const BLOCKED_SUFFIXES: &[&str] = &[
    ".localhost",
    ".local",
    ".internal",
    ".localdomain",
    ".lan",
    ".home.arpa",
    ".corp",
    ".nip.io",
    ".sslip.io",
    ".xip.io",
];

pub fn web_url(url: &Url) -> bool {
    matches!(url.scheme(), "http" | "https")
        && url.username().is_empty()
        && url.password().is_none()
        && !url.port().is_some_and(|p| BLOCKED_PORTS.contains(&p))
        && url.host_str().is_some_and(|h| {
            let host = h
                .trim_matches(['[', ']'])
                .trim_end_matches('.')
                .to_ascii_lowercase();
            host.parse::<IpAddr>().map(public_ip).unwrap_or_else(|_| {
                host.contains('.')
                    && !host.split('.').any(str::is_empty)
                    && !BLOCKED_SUFFIXES
                        .iter()
                        .any(|s| host.ends_with(s) || host == s[1..])
            })
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_rfc9637_ipv6_internal_domains_empty_labels_and_non_web_ports() {
        assert!(!public_ip("3fff::1".parse().unwrap()));
        assert!(!public_ip("3fff:0fff:ffff::1".parse().unwrap()));
        assert!(public_ip("3fff:1000::1".parse().unwrap()));
        for bad in [
            "https://a..b.com/",
            "https://service.internal/",
            "https://host.localdomain/",
            "https://printer.lan/",
            "https://router.home.arpa/",
            "https://intranet.corp/",
            "https://127.0.0.1.nip.io/",
            "https://10.0.0.1.sslip.io/",
            "https://127.0.0.1.xip.io/",
            "http://example.com:22/",
            "http://example.com:5432/",
            "http://example.com:6379/",
        ] {
            assert!(!web_url(&Url::parse(bad).unwrap()), "should reject {bad}");
        }
        for good in [
            "https://example.com/path",
            "http://example.org:8080/media",
            "https://sub.example.test/cover.webp",
        ] {
            assert!(web_url(&Url::parse(good).unwrap()), "should allow {good}");
        }
    }
}
