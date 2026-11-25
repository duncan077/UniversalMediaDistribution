CREATE TABLE schema_info (
            key TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );

        INSERT INTO schema_info (key, value) VALUES
            ('application_id', 'universal-media-distribution'),
            ('schema_version', '1'),
            ('catalog_revision', '0');

        CREATE TABLE catalog_items (
            id TEXT NOT NULL PRIMARY KEY,
            media_type TEXT NOT NULL CHECK (media_type IN ('movie', 'book', 'audio')),
            subtype_code TEXT NOT NULL,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL,
            original_name TEXT NULL,
            sort_name TEXT NULL,
            description TEXT NOT NULL DEFAULT '',
            release_date TEXT NULL,
            end_date TEXT NULL,
            publication_status INTEGER NOT NULL DEFAULT 0,
            original_language TEXT NULL,
            country_code TEXT NULL,
            content_rating TEXT NULL,
            runtime_seconds INTEGER NULL CHECK (runtime_seconds IS NULL OR runtime_seconds >= 0),
            page_count INTEGER NULL CHECK (page_count IS NULL OR page_count >= 0),
            thumbnail_ref TEXT NULL,
            backdrop_ref TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE (media_type, name_key)
        );

        CREATE INDEX ix_catalog_items_type_name ON catalog_items (media_type, name);

        CREATE TABLE catalog_aliases (
            id TEXT NOT NULL PRIMARY KEY,
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL,
            language TEXT NOT NULL DEFAULT '',
            alias_type TEXT NOT NULL DEFAULT 'other',
            UNIQUE (catalog_item_id, name_key, language, alias_type)
        );

        CREATE TABLE genres (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL UNIQUE
        );

        CREATE TABLE catalog_item_genres (
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            genre_id TEXT NOT NULL REFERENCES genres(id) ON DELETE CASCADE,
            PRIMARY KEY (catalog_item_id, genre_id)
        );

        CREATE TABLE contributors (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL,
            kind TEXT NOT NULL CHECK (kind IN ('person', 'organization')),
            thumbnail_ref TEXT NULL,
            UNIQUE (kind, name_key)
        );

        CREATE TABLE catalog_item_credits (
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            contributor_id TEXT NOT NULL REFERENCES contributors(id) ON DELETE CASCADE,
            role TEXT NOT NULL,
            character_name TEXT NOT NULL DEFAULT '',
            sort_order INTEGER NOT NULL DEFAULT 0 CHECK (sort_order >= 0),
            PRIMARY KEY (catalog_item_id, contributor_id, role, character_name)
        );

        CREATE TABLE seasons (
            id TEXT NOT NULL PRIMARY KEY,
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            season_number INTEGER NOT NULL CHECK (season_number >= 0),
            display_name TEXT NULL,
            thumbnail_ref TEXT NULL,
            release_date TEXT NULL,
            sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
            UNIQUE (catalog_item_id, season_number)
        );

        CREATE TABLE content_units (
            id TEXT NOT NULL PRIMARY KEY,
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            season_id TEXT NULL REFERENCES seasons(id) ON DELETE CASCADE,
            unit_kind TEXT NOT NULL CHECK (unit_kind IN ('episode', 'chapter', 'track', 'audio_chapter')),
            display_number TEXT NOT NULL,
            display_number_key TEXT NOT NULL,
            name TEXT NULL,
            thumbnail_ref TEXT NULL,
            duration_seconds INTEGER NULL CHECK (duration_seconds IS NULL OR duration_seconds >= 0),
            page_count INTEGER NULL CHECK (page_count IS NULL OR page_count >= 0),
            release_date TEXT NULL,
            is_special INTEGER NOT NULL DEFAULT 0 CHECK (is_special IN (0, 1)),
            sort_order INTEGER NOT NULL CHECK (sort_order >= 0)
        );

        CREATE UNIQUE INDEX ux_content_units_direct
            ON content_units (catalog_item_id, unit_kind, display_number_key)
            WHERE season_id IS NULL;

        CREATE UNIQUE INDEX ux_content_units_season
            ON content_units (season_id, unit_kind, display_number_key)
            WHERE season_id IS NOT NULL;

        CREATE TABLE distribution_rights (
            id TEXT NOT NULL PRIMARY KEY,
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            natural_key TEXT NOT NULL,
            source_name TEXT NOT NULL,
            source_uri TEXT NOT NULL,
            license_name TEXT NOT NULL,
            license_uri TEXT NULL,
            attribution TEXT NULL,
            rights_note TEXT NULL,
            verified_at TEXT NULL,
            UNIQUE (catalog_item_id, natural_key)
        );

        CREATE TABLE torrent_packages (
            id TEXT NOT NULL PRIMARY KEY,
            natural_key TEXT NOT NULL UNIQUE,
            display_name TEXT NULL,
            magnet_uri TEXT NULL,
            torrent_metadata BLOB NULL,
            info_hash_v1 TEXT NULL,
            info_hash_v2 TEXT NULL,
            is_private INTEGER NOT NULL DEFAULT 0 CHECK (is_private IN (0, 1)),
            total_size INTEGER NULL CHECK (total_size IS NULL OR total_size >= 0),
            source_uri TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            CHECK (magnet_uri IS NOT NULL OR torrent_metadata IS NOT NULL OR info_hash_v1 IS NOT NULL OR info_hash_v2 IS NOT NULL)
        );

        CREATE TABLE tracker_endpoints (
            id TEXT NOT NULL PRIMARY KEY,
            natural_key TEXT NOT NULL UNIQUE,
            name TEXT NULL,
            endpoint_template TEXT NOT NULL,
            transport TEXT NOT NULL CHECK (transport IN ('https', 'wss', 'udp')),
            client_family TEXT NOT NULL CHECK (client_family IN ('native', 'browser', 'both')),
            requires_authentication INTEGER NOT NULL DEFAULT 0 CHECK (requires_authentication IN (0, 1)),
            is_enabled INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1))
        );

        CREATE TABLE torrent_package_trackers (
            torrent_package_id TEXT NOT NULL REFERENCES torrent_packages(id) ON DELETE CASCADE,
            tracker_endpoint_id TEXT NOT NULL REFERENCES tracker_endpoints(id) ON DELETE CASCADE,
            tier INTEGER NOT NULL DEFAULT 0 CHECK (tier >= 0),
            sort_order INTEGER NOT NULL DEFAULT 0 CHECK (sort_order >= 0),
            PRIMARY KEY (torrent_package_id, tracker_endpoint_id)
        );

        CREATE TABLE media_sources (
            id TEXT NOT NULL PRIMARY KEY,
            natural_key TEXT NOT NULL UNIQUE,
            torrent_package_id TEXT NOT NULL REFERENCES torrent_packages(id) ON DELETE CASCADE,
            catalog_item_id TEXT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            content_unit_id TEXT NULL REFERENCES content_units(id) ON DELETE CASCADE,
            asset_role TEXT NOT NULL,
            source_layout TEXT NOT NULL CHECK (source_layout IN ('single_file', 'image_sequence')),
            file_index INTEGER NULL CHECK (file_index IS NULL OR file_index >= 0),
            file_path TEXT NOT NULL DEFAULT '',
            mime_type TEXT NULL,
            container TEXT NULL,
            video_quality TEXT NULL,
            language TEXT NULL,
            size_bytes INTEGER NULL CHECK (size_bytes IS NULL OR size_bytes >= 0),
            sort_order INTEGER NOT NULL DEFAULT 0 CHECK (sort_order >= 0),
            CHECK ((catalog_item_id IS NOT NULL AND content_unit_id IS NULL) OR
                   (catalog_item_id IS NULL AND content_unit_id IS NOT NULL))
        );

        CREATE TABLE library_entries (
            catalog_item_id TEXT NOT NULL PRIMARY KEY REFERENCES catalog_items(id) ON DELETE CASCADE,
            is_favorite INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0, 1)),
            added_at TEXT NOT NULL,
            last_opened_at TEXT NULL,
            user_rating REAL NULL
        );

        CREATE TABLE playback_progress (
            id TEXT NOT NULL PRIMARY KEY,
            catalog_item_id TEXT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            content_unit_id TEXT NULL REFERENCES content_units(id) ON DELETE CASCADE,
            position REAL NOT NULL DEFAULT 0 CHECK (position >= 0),
            position_kind TEXT NOT NULL,
            is_completed INTEGER NOT NULL DEFAULT 0 CHECK (is_completed IN (0, 1)),
            updated_at TEXT NOT NULL,
            CHECK ((catalog_item_id IS NOT NULL AND content_unit_id IS NULL) OR
                   (catalog_item_id IS NULL AND content_unit_id IS NOT NULL))
        );

        CREATE TABLE reader_preferences (
            catalog_item_id TEXT NOT NULL PRIMARY KEY REFERENCES catalog_items(id) ON DELETE CASCADE,
            display_mode TEXT NOT NULL CHECK (display_mode IN ('paged', 'continuous')),
            updated_at TEXT NOT NULL
        );

        CREATE TABLE download_jobs (
            id TEXT NOT NULL PRIMARY KEY,
            torrent_package_id TEXT NOT NULL REFERENCES torrent_packages(id) ON DELETE CASCADE,
            state TEXT NOT NULL,
            selected_file_indexes TEXT NULL,
            download_location TEXT NULL,
            bytes_downloaded INTEGER NOT NULL DEFAULT 0 CHECK (bytes_downloaded >= 0),
            error_code TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE catalog_import_history (
            id TEXT NOT NULL PRIMARY KEY,
            document_hash TEXT NULL,
            source_name TEXT NULL,
            format_version INTEGER NOT NULL,
            created_count INTEGER NOT NULL CHECK (created_count >= 0),
            updated_count INTEGER NOT NULL CHECK (updated_count >= 0),
            warning_count INTEGER NOT NULL DEFAULT 0 CHECK (warning_count >= 0),
            imported_at TEXT NOT NULL
        );

        PRAGMA user_version = 1;
