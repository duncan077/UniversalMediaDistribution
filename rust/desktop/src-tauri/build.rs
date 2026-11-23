fn main() {
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        std::fs::create_dir_all("../../target/vlc-active").expect("VLC staging directory");
        println!("cargo:rerun-if-changed=src/windows_audio.cpp");
        cc::Build::new()
            .cpp(true)
            .static_crt(true)
            .file("src/windows_audio.cpp")
            .flag_if_supported("/std:c++17")
            .flag_if_supported("/EHsc")
            .compile("umd_audio");
        for lib in ["ole32", "uuid"] {
            println!("cargo:rustc-link-lib={lib}");
        }
    }
    tauri_build::build();
}
