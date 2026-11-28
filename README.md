# Universal Media Distribution

Cliente Tauri y servidor Rust para organizar, leer y reproducir contenido multimedia. UMD funciona en Windows, Linux, Android y Android TV.

## Descargas

Los ZIP de Windows y Linux se descomprimen y ejecutan directamente. Android y Android TV usan el APK firmado. El servidor se distribuye por separado para Linux x64 y ARM64.

[Versiones y descargas](https://github.com/duncan077/UniversalMediaDistribution/releases)

## Desarrollo

Desde la raíz del repositorio:

```sh
npm --prefix rust/desktop ci
npm --prefix rust/desktop run tauri dev
cargo run --manifest-path rust/Cargo.toml -p umd-server
```

La [guía de Rust](rust/README.md) describe los requisitos de compilación, el empaquetado, las actualizaciones y la migración de bibliotecas y servidores existentes.

La última versión del proyecto .NET está archivada en la [rama legacy](https://github.com/duncan077/UniversalMediaDistribution/tree/legacy).

## Licencia

Universal Media Distribution se distribuye bajo la [Licencia Apache 2.0](LICENSE).
