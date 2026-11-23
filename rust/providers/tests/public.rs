use umd_providers::{Provider, Site};

#[tokio::test]
#[ignore = "consulta los catálogos públicos de los proveedores"]
async fn public_catalog_and_title() {
    let ids=std::env::var("UMD_TEST_PROVIDERS").unwrap_or_else(|_|"manhwaweb,jkanime,doramasflix,leercapitulo,zonatmo,uchuujinmangas,animeav1,olympusxyz,animetmo,framextv,pelisplus,pelisplus-to,pelisflix-hd,poseidon-hd2,seriesflix".into());
    let mut jobs = tokio::task::JoinSet::new();
    for id in ids.split(',').map(str::to_owned) {
        jobs.spawn(async move {
            let run = async {
                let mut source = Provider::open(Site::parse(&id).unwrap()).await?;
                let page = source.page(None).await?;
                assert!(!page.items.is_empty());
                println!("{id}: directorio con {} entradas", page.items.len());
                let item = source.detail(&page.items[0]).await?;
                item.validate().unwrap();
                let count = |v: &serde_json::Value| v.as_array().map_or(0, Vec::len);
                let units = item.metadata.get("units").map_or(0, count)
                    + item
                        .metadata
                        .get("seasons")
                        .and_then(|v| v.as_array())
                        .into_iter()
                        .flatten()
                        .map(|s| count(&s["units"]))
                        .sum::<usize>();
                println!(
                    "{id}: {} títulos, ficha válida, {units} unidades",
                    page.items.len()
                );
                Ok::<(), umd_providers::Failure>(())
            };
            let result = tokio::time::timeout(std::time::Duration::from_secs(120), run).await;
            (id, result)
        });
    }
    let mut failed = vec![];
    while let Some(result) = jobs.join_next().await {
        let (id, result) = result.unwrap();
        if !matches!(result, Ok(Ok(()))) {
            failed.push((id, result))
        }
    }
    assert!(failed.is_empty(), "{failed:?}");
}
