use super::{accounts::ClientAddress, ApiError, ApiResult};
use axum::{
    extract::{Request, State},
    http::{header, HeaderValue, Method, StatusCode},
    middleware::Next,
    response::{IntoResponse, Response},
};
use std::{
    collections::HashMap,
    io,
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

#[derive(Clone, Copy)]
pub(crate) struct Limits {
    pub json: usize,
    pub iterations: u32,
    pub days: i64,
    pub requests: u32,
}
impl Limits {
    pub fn from_env() -> umd_core::Result<Self> {
        fn number(name: &str, default: usize, min: usize, max: usize) -> umd_core::Result<usize> {
            match std::env::var(name) {
                Ok(s) if !s.trim().is_empty() => s
                    .trim()
                    .parse()
                    .ok()
                    .filter(|n| (min..=max).contains(n))
                    .ok_or_else(|| {
                        umd_core::Error::Invalid(format!("{name} debe estar entre {min} y {max}."))
                    }),
                _ => Ok(default),
            }
        }
        Ok(Self {
            json: number("UMD_SERVER_MAX_JSON_BYTES", 40 << 20, 1024, 256 << 20)?,
            iterations: number("UMD_SERVER_PASSWORD_ITERATIONS", 600000, 100000, 2000000)? as u32,
            days: number("UMD_SERVER_SESSION_DAYS", 30, 1, 365)? as i64,
            requests: number("UMD_SERVER_CATALOG_REQUESTS_PER_MINUTE", 60, 1, 6000)? as u32,
        })
    }
}
pub async fn no_store(mut response: Response) -> Response {
    response
        .headers_mut()
        .insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    response
}
#[derive(Clone)]
pub struct Rate {
    limit: u32,
    window: Arc<Mutex<(Instant, HashMap<String, u32>)>>,
}
impl Rate {
    pub fn new(limit: u32) -> Self {
        Self {
            limit,
            window: Arc::new(Mutex::new((Instant::now(), HashMap::new()))),
        }
    }
    fn retry(&self, address: String) -> Option<u64> {
        let mut window = self.window.lock().unwrap_or_else(|p| p.into_inner());
        let elapsed = window.0.elapsed();
        if elapsed >= Duration::from_secs(60) {
            window.0 = Instant::now();
            window.1.clear();
        }
        let wait = 60 - window.0.elapsed().as_secs().min(59);
        // Keep the number of per-address counters bounded during bursts.
        if window.1.len() >= 100_000 && !window.1.contains_key(&address) {
            return Some(wait);
        }
        let count = window.1.entry(address).or_default();
        if *count >= self.limit {
            return Some(wait);
        }
        *count += 1;
        None
    }
}
pub async fn catalog_rate(
    State(rate): State<Rate>,
    ClientAddress(ip): ClientAddress,
    request: Request,
    next: Next,
) -> Response {
    if matches!(*request.method(), Method::GET | Method::HEAD) {
        if let Some(wait) = rate.retry(ip) {
            return (StatusCode::TOO_MANY_REQUESTS, [(header::RETRY_AFTER,wait.to_string())], axum::Json(serde_json::json!({"code":"429","message":"Se alcanzó el límite de consultas al catálogo."}))).into_response();
        }
    }
    next.run(request).await
}
pub fn catalog_json(value: &impl serde::Serialize, max: usize) -> ApiResult {
    struct Bounded {
        bytes: Vec<u8>,
        max: usize,
    }
    impl io::Write for Bounded {
        fn write(&mut self, data: &[u8]) -> io::Result<usize> {
            if data.len() > self.max - self.bytes.len() {
                return Err(io::ErrorKind::FileTooLarge.into());
            }
            self.bytes.extend_from_slice(data);
            Ok(data.len())
        }
        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }
    let mut writer = Bounded {
        bytes: Vec::new(),
        max,
    };
    serde_json::to_writer(&mut writer, value).map_err(|e| {
        if e.io_error_kind() == Some(io::ErrorKind::FileTooLarge) {
            ApiError(
                StatusCode::PAYLOAD_TOO_LARGE,
                "El catálogo supera el tamaño permitido. Consultá una página más pequeña.".into(),
            )
        } else {
            ApiError(
                StatusCode::INTERNAL_SERVER_ERROR,
                "No se pudo serializar el catálogo.".into(),
            )
        }
    })?;
    Ok(([(header::CONTENT_TYPE, "application/json")], writer.bytes).into_response())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn catalog_bounds_encoded_bytes_and_keeps_ip_counters_separate() {
        assert!(catalog_json(&"é", 4).is_ok());
        assert!(matches!(
            catalog_json(&"é", 3),
            Err(ApiError(StatusCode::PAYLOAD_TOO_LARGE, _))
        ));
        let rate = Rate::new(1);
        assert_eq!(rate.retry("192.0.2.1".into()), None);
        assert!(rate.retry("192.0.2.1".into()).is_some());
        assert_eq!(rate.retry("192.0.2.2".into()), None);
        rate.window.lock().unwrap().0 = Instant::now() - Duration::from_secs(61);
        assert_eq!(rate.retry("192.0.2.1".into()), None);
    }
}
