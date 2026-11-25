use reqwest::header::{HeaderMap, HeaderValue, ACCEPT_LANGUAGE};
use reqwest::{Client, ClientBuilder};

pub const PAGE_ACCEPT: &str = concat!(
    "text/html,application/xhtml+xml,application/xml;q=0.9,",
    "image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7"
);

const USER_AGENT: &str = concat!(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ",
    "(KHTML, like Gecko) Chrome/158.0.0.0 Safari/537.36"
);

/// HTTP headers for provider requests shared by the app and the crawler.
pub fn client_builder() -> ClientBuilder {
    let mut headers = HeaderMap::new();
    headers.insert(
        ACCEPT_LANGUAGE,
        HeaderValue::from_static("es-ES,es;q=0.9,en;q=0.7"),
    );
    Client::builder()
        .user_agent(USER_AGENT)
        .default_headers(headers)
}
