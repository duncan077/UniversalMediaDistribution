use serde_json::{json, Value};
use umd_core::{validate_backup, CatalogDocument, CatalogStore, ProgressTarget};

#[test]
fn saving_a_public_list_is_atomic_repeatable_and_preserves_local_titles() {
    let (local, target) = store();
    let mut imported = local.export().unwrap();
    imported.items[0].name = "Nombre remoto".into();
    let second = uuid::Uuid::new_v4();
    let mut added = imported.items[0].clone();
    added.sync_id = Some(second);
    added.name = "Nueva".into();
    imported.items.push(added);
    let link = "https://example.test/api/v1/shared-lists/example";
    let mut invalid = imported.clone();
    invalid.items[1].deleted = true;
    assert!(local
        .save_shared_list(link, "Compartida", &invalid)
        .is_err());
    assert!(local.personal_lists().unwrap().is_empty());
    assert!(local
        .save_shared_list(link, "Compartida", &imported)
        .unwrap());
    assert!(!local
        .save_shared_list(link, "Compartida", &imported)
        .unwrap());
    let lists = local.personal_lists().unwrap();
    assert_eq!(lists.len(), 1);
    assert_eq!(lists[0]["items"][0]["itemId"], target.item_id.to_string());
    assert_ne!(lists[0]["items"][0]["name"], "Nombre remoto");
    assert_eq!(lists[0]["items"][1]["itemId"], second.to_string());
    assert_eq!(local.export().unwrap().items.len(), 2);
    assert!(local.backup().is_ok());
}

#[test]
fn syncing_two_devices_preserves_independent_edits_and_converges() {
    use umd_core::ListEdit;
    let (phone, target) = store();
    phone
        .edit_list(ListEdit::Create {
            name: "Familia".into(),
        })
        .unwrap();
    phone.set_favorite(target.item_id, true).unwrap();
    let (_, seed) = phone.reconcile_profile(None).unwrap();
    let desktop = CatalogStore::in_memory().unwrap();
    let (changed, outgoing) = desktop.reconcile_profile(seed.as_ref()).unwrap();
    assert!(changed && outgoing.is_none());
    let list = desktop.personal_lists().unwrap()[0]["id"]
        .as_str()
        .unwrap()
        .parse()
        .unwrap();
    desktop
        .save_progress(&target, 17.0, "seconds", false)
        .unwrap();
    desktop
        .edit_list(ListEdit::Member {
            id: list,
            item_id: target.item_id,
            enabled: true,
        })
        .unwrap();
    phone
        .reader_mode(target.item_id, Some("continuous"))
        .unwrap();
    phone
        .edit_list(ListEdit::Rename {
            id: list,
            name: "Compartida".into(),
        })
        .unwrap();
    phone.set_favorite(target.item_id, false).unwrap();
    let (_, phone_doc) = phone.reconcile_profile(seed.as_ref()).unwrap();
    let (_, merged) = desktop.reconcile_profile(phone_doc.as_ref()).unwrap();
    let merged = merged.unwrap();
    let (changed, outgoing) = phone.reconcile_profile(Some(&merged)).unwrap();
    assert!(changed && outgoing.is_none());
    assert_eq!(phone.progress(&target).unwrap().unwrap()["position"], 17.0);
    assert_eq!(
        desktop.reader_mode(target.item_id, None).unwrap(),
        "continuous"
    );
    assert_eq!(merged["favoriteItemStates"][0]["isFavorite"], false);
    assert_eq!(merged["lists"][0]["items"].as_array().unwrap().len(), 1);
    assert_eq!(merged["lists"][0]["name"], "Compartida");
    assert_eq!(
        phone.personal_lists().unwrap(),
        desktop.personal_lists().unwrap()
    );
    for store in [&phone, &desktop] {
        assert_eq!(
            store.reconcile_profile(Some(&merged)).unwrap(),
            (false, None)
        );
    }
    desktop.edit_list(ListEdit::Delete { id: list }).unwrap();
    let (_, removed) = desktop.reconcile_profile(Some(&merged)).unwrap();
    phone.reconcile_profile(removed.as_ref()).unwrap();
    assert!(phone.personal_lists().unwrap().is_empty());
    assert_ne!(
        phone.backup().unwrap()["deviceId"],
        desktop.backup().unwrap()["deviceId"]
    );
}

#[test]
fn an_oversized_local_library_does_not_block_account_favorites_or_progress() {
    let (remote, target) = store();
    remote.set_favorite(target.item_id, true).unwrap();
    let snapshot = remote.backup().unwrap();

    let (local, _) = store();
    let original = local.export().unwrap().items.remove(0);
    let unrelated: Vec<_> = (0..12)
        .map(|n| {
            let mut item = original.clone();
            item.sync_id = Some(uuid::Uuid::new_v4());
            item.name = format!("Título local {n}");
            item.metadata
                .insert("description".into(), json!("x".repeat(4096)));
            item
        })
        .collect();
    local.import(&CatalogDocument::new(unrelated), false).unwrap();
    local.save_progress(&target, 7.0, "page", true).unwrap();

    let limit = 32 * 1024;
    assert!(local
        .reconcile_profile_with_limit(Some(&snapshot), limit)
        .is_err());
    assert!(local
        .page(&umd_core::CatalogQuery::default())
        .unwrap()
        .favorite_ids
        .is_empty());

    let (changed, upload) = local
        .reconcile_account_profile(Some(&snapshot), limit)
        .unwrap();
    assert!(changed);
    let merged = upload.unwrap();
    let catalog = validate_backup(&merged).unwrap();
    assert_eq!(catalog.items.len(), 1);
    assert_eq!(merged["favoriteItemStates"][0]["isFavorite"], true);
    assert_eq!(merged["progress"][0]["position"], 7.0);
    assert_eq!(local.export().unwrap().items.len(), 13);
    assert_eq!(
        local.page(&umd_core::CatalogQuery::default()).unwrap().favorite_ids,
        vec![target.item_id]
    );
    assert_eq!(
        local.reconcile_account_profile(Some(&merged), limit).unwrap(),
        (false, None)
    );
}

#[test]
fn personal_backups_skip_unrelated_titles_in_both_libraries() {
    let (remote, target) = store();
    let mut unrelated = remote.export().unwrap().items.remove(0);
    unrelated.name = "Catálogo de otro dispositivo".into();
    unrelated.sync_id = Some(uuid::Uuid::new_v4());
    remote.import(&CatalogDocument::new(vec![unrelated.clone()]), false).unwrap();
    remote.set_favorite(target.item_id, true).unwrap();
    let legacy = remote.backup().unwrap();

    let local = CatalogStore::in_memory().unwrap();
    unrelated.name = "Catálogo local".into();
    unrelated.sync_id = Some(uuid::Uuid::new_v4());
    let local_title = unrelated.sync_id.unwrap();
    local.import(&CatalogDocument::new(vec![unrelated]), false).unwrap();
    let limit = umd_core::MAX_DOCUMENT_BYTES;
    let (_, initial) = local.reconcile_account_profile(None, limit).unwrap();
    assert_eq!(initial.unwrap()["catalog"]["items"], json!([]));

    let (changed, outgoing) = local.reconcile_account_profile(Some(&legacy), limit).unwrap();
    assert!(changed && outgoing.is_none());
    assert_eq!(local.export().unwrap().items.len(), 2);
    assert!(local.item(local_title).unwrap().is_some());
    assert_eq!(local.page(&umd_core::CatalogQuery::default()).unwrap().favorite_ids, vec![target.item_id]);

    local.save_progress(&target, 7.0, "page", true).unwrap();
    let (_, personal) = local.reconcile_account_profile(Some(&legacy), limit).unwrap();
    let personal = personal.unwrap();
    assert_eq!(personal["catalog"]["items"].as_array().unwrap().len(), 1);
    let patch = umd_core::account_backup_patch(&legacy, &personal).unwrap();
    assert_eq!(patch["catalog"]["items"].as_array().unwrap().len(), 1);
    assert_eq!(patch["progress"][0]["position"], 7.0);

    local.restore_account_backup(&legacy, "https://example.test/", uuid::Uuid::new_v4(), 1).unwrap();
    assert!(local.progress(&target).unwrap().is_none());
    assert_eq!(local.export().unwrap().items.len(), 2);
    assert!(local.item(local_title).unwrap().is_some());
}

#[test]
fn account_sync_folds_distinct_dotnet_records_linked_by_a_redirect() {
    let (remote, target) = store();
    remote.set_favorite(target.item_id, true).unwrap();
    let mut snapshot = remote.backup().unwrap();
    let old_id = uuid::Uuid::new_v4();
    let mut old_title = snapshot["catalog"]["items"][0].clone();
    old_title["syncId"] = json!(old_id);
    snapshot["catalog"]["items"].as_array_mut().unwrap().push(old_title);

    let mut old_state = snapshot["favoriteItemStates"][0].clone();
    old_state["item"]["itemId"] = json!(old_id);
    old_state["isFavorite"] = json!(false);
    let clock = old_state["syncVersion"]["clock"].as_i64().unwrap();
    snapshot["favoriteItemStates"][0]["syncVersion"]["clock"] = json!(clock + 1);
    snapshot["catalogRedirects"] = json!([{
        "fromId": old_id,
        "toId": target.item_id,
        "syncVersion": old_state["syncVersion"]
    }]);
    snapshot["favoriteItemStates"].as_array_mut().unwrap().push(old_state);
    assert!(validate_backup(&snapshot).is_err());

    let receiving = CatalogStore::in_memory().unwrap();
    let limit = umd_core::MAX_DOCUMENT_BYTES;
    let (changed, upload) = receiving.reconcile_account_profile(Some(&snapshot), limit).unwrap();
    assert!(changed);
    assert!(upload.is_none());
    assert_eq!(receiving.reconcile_account_profile(Some(&snapshot), limit).unwrap(), (false, None));
    receiving.save_progress(&target, 7.0, "page", true).unwrap();
    let (_, upload) = receiving.reconcile_account_profile(Some(&snapshot), limit).unwrap();
    let merged = upload.unwrap();
    assert!(validate_backup(&merged).is_ok());
    assert_eq!(merged["favoriteItemStates"].as_array().unwrap().len(), 1);
    assert_eq!(merged["favoriteItemStates"][0]["isFavorite"], true);
    assert_eq!(receiving.page(&umd_core::CatalogQuery::default()).unwrap().favorite_ids, vec![target.item_id]);

    let exact_duplicate = snapshot["favoriteItemStates"][0].clone();
    snapshot["favoriteItemStates"].as_array_mut().unwrap().push(exact_duplicate);
    assert!(umd_core::validate_account_backup(&snapshot, limit).is_err());
}

#[test]
fn dotnet_list_order_and_equivalent_timestamps_do_not_trigger_an_upload() {
    use umd_core::ListEdit;
    let (library, _) = store();
    for name in ["Una", "Otra"] {
        library
            .edit_list(ListEdit::Create { name: name.into() })
            .unwrap();
    }
    let mut remote = library.reconcile_profile(None).unwrap().1.unwrap();
    remote["lists"].as_array_mut().unwrap().reverse();
    for list in remote["lists"].as_array_mut().unwrap() {
        let stamp = list["syncVersion"]["modifiedAt"]
            .as_str()
            .unwrap()
            .replace('Z', "+00:00");
        list["syncVersion"]["modifiedAt"] = json!(stamp);
    }
    for _ in 0..2 {
        assert!(library
            .reconcile_profile(Some(&remote))
            .unwrap()
            .1
            .is_none());
    }
}

#[test]
fn synchronizing_legacy_names_and_equal_version_removals_is_repeatable() {
    let (first, target) = store();
    first.set_favorite(target.item_id, true).unwrap();
    let mut remote = first.backup().unwrap();
    remote["favoriteItemStates"][0]["isFavorite"] = json!(false);
    remote["version"] = json!(1);
    remote["favoriteContributorKeys"] = json!(["translator"]);
    remote["favoriteItemStates"][0]["item"]
        .as_object_mut()
        .unwrap()
        .remove("itemId");
    let (_, output) = first.reconcile_profile(Some(&remote)).unwrap();
    let output = output.unwrap();
    assert_eq!(output["favoriteItemStates"][0]["isFavorite"], false);
    assert_eq!(output["favoriteContributorKeys"], json!(["translator"]));
    assert_eq!(output["version"], 4);
    assert_eq!(
        first.reconcile_profile(Some(&output)).unwrap(),
        (false, None)
    );
}

#[test]
fn sync_preserves_remote_tombstones_unknown_fields_and_other_local_profiles() {
    use umd_core::{CatalogQuery, ProfileEdit};
    let (first, target) = store();
    first.set_favorite(target.item_id, true).unwrap();
    first
        .edit_profile(ProfileEdit::Create {
            name: "Otro".into(),
        })
        .unwrap();
    let other = first
        .for_profile(first.profiles().unwrap().profiles[1].id.parse().unwrap())
        .unwrap();
    other.set_favorite(target.item_id, true).unwrap();
    let mut remote = first.backup().unwrap();
    remote["catalog"]["items"][0]["deleted"] = json!(true);
    remote["futureField"] = json!({"keep":true});
    remote["catalog"]["futureCatalogField"] = json!(13);
    let (_, outgoing) = first.reconcile_profile(Some(&remote)).unwrap();
    let outgoing = outgoing.unwrap();
    assert_eq!(outgoing["futureField"], remote["futureField"]);
    assert_eq!(outgoing["catalog"], remote["catalog"]);
    assert!(outgoing["favoriteItemStates"]
        .as_array()
        .unwrap()
        .is_empty());
    assert_eq!(
        other.page(&CatalogQuery::default()).unwrap().favorite_ids,
        vec![target.item_id]
    );
    assert_eq!(first.export().unwrap().items.len(), 1);
}

#[test]
fn profiles_share_titles_and_isolate_personal_data_and_backup_revisions() {
    use umd_core::{CatalogQuery, ListEdit, ProfileEdit};
    let (first, target) = store();
    let account = uuid::Uuid::new_v4();
    let origin = "https://catalog.example/";
    first.set_favorite(target.item_id, true).unwrap();
    first
        .save_progress(&target, 41.0, "seconds", false)
        .unwrap();
    first
        .reader_mode(target.item_id, Some("continuous"))
        .unwrap();
    first
        .edit_list(ListEdit::Create {
            name: "Ver después".into(),
        })
        .unwrap();
    first.remember_upload(origin, account, 7).unwrap();
    first
        .edit_profile(ProfileEdit::Create {
            name: "Lucía".into(),
        })
        .unwrap();
    assert!(first
        .edit_profile(ProfileEdit::Create {
            name: " lucia ".into()
        })
        .is_err());
    let id = first.profiles().unwrap().profiles[1].id.parse().unwrap();
    let second = first.for_profile(id).unwrap();
    first.edit_profile(ProfileEdit::Select { id }).unwrap();
    assert_eq!(second.export().unwrap().items.len(), 1);
    assert!(second
        .page(&CatalogQuery::default())
        .unwrap()
        .favorite_ids
        .is_empty());
    assert!(second.progress(&target).unwrap().is_none());
    assert!(second.personal_lists().unwrap().is_empty());
    assert_eq!(second.reader_mode(target.item_id, None).unwrap(), "paged");
    assert_eq!(second.backup_revision(origin, account).unwrap(), None);
    // A late write from the first window still belongs to its original profile.
    first
        .save_progress(&target, 42.0, "seconds", false)
        .unwrap();
    assert!(second.progress(&target).unwrap().is_none());
    let blank = CatalogStore::in_memory().unwrap().backup().unwrap();
    second
        .restore_account_backup(&blank, origin, account, 1)
        .unwrap();
    assert_eq!(first.export().unwrap().items.len(), 1);
    assert_eq!(
        first.page(&CatalogQuery::default()).unwrap().favorite_ids,
        vec![target.item_id]
    );
    assert_eq!(first.progress(&target).unwrap().unwrap()["position"], 42.0);
    assert_eq!(first.backup_revision(origin, account).unwrap(), Some(7));
    assert_eq!(second.backup_revision(origin, account).unwrap(), Some(1));
    first
        .edit_profile(ProfileEdit::Rename {
            id,
            name: "Mar".into(),
        })
        .unwrap();
    assert_eq!(first.profiles().unwrap().profiles[1].name, "Mar");
    first.edit_profile(ProfileEdit::Delete { id }).unwrap();
    assert!(second
        .save_progress(&target, 1.0, "seconds", false)
        .is_err());
    let state = first.profiles().unwrap();
    assert_eq!(state.active_id, state.profiles[0].id);
    assert!(first
        .edit_profile(ProfileEdit::Delete {
            id: state.active_id.parse().unwrap()
        })
        .is_err());
}

#[test]
fn upgrading_an_existing_library_preserves_personal_state_and_selection_across_restarts() {
    use umd_core::ProfileEdit;
    let (original, target) = store();
    original.set_favorite(target.item_id, true).unwrap();
    original
        .save_progress(&target, 12.0, "seconds", false)
        .unwrap();
    let mut saved = original.backup().unwrap();
    let catalog = original.export().unwrap();
    saved.as_object_mut().unwrap().remove("catalog");
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("library.db");
    let account = uuid::Uuid::new_v4();
    {
        let db = rusqlite::Connection::open(&path).unwrap();
        db.execute_batch("CREATE TABLE rust_catalog(id TEXT PRIMARY KEY,name_key TEXT NOT NULL,media_type TEXT NOT NULL,search_text TEXT NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE rust_favorites(id TEXT PRIMARY KEY REFERENCES rust_catalog(id) ON DELETE CASCADE);
            CREATE TABLE rust_settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE rust_backup_revisions(server TEXT,account TEXT,revision INTEGER,PRIMARY KEY(server,account));").unwrap();
        db.execute(
            "INSERT INTO rust_catalog VALUES(?1,'film','movie','film',?2)",
            rusqlite::params![
                target.item_id.to_string(),
                serde_json::to_string(&catalog.items[0]).unwrap()
            ],
        )
        .unwrap();
        db.execute(
            "INSERT INTO rust_favorites VALUES(?1)",
            [target.item_id.to_string()],
        )
        .unwrap();
        db.execute(
            "INSERT INTO rust_settings VALUES('user_document',?1)",
            [saved.to_string()],
        )
        .unwrap();
        db.execute(
            "INSERT INTO rust_backup_revisions VALUES('server',?1,3)",
            [account.to_string()],
        )
        .unwrap();
    }
    let first = CatalogStore::open(&path).unwrap();
    assert_eq!(first.progress(&target).unwrap().unwrap()["position"], 12.0);
    assert_eq!(first.backup_revision("server", account).unwrap(), Some(3));
    assert_eq!(
        first
            .page(&umd_core::CatalogQuery::default())
            .unwrap()
            .favorite_ids,
        vec![target.item_id]
    );
    first
        .edit_profile(ProfileEdit::Create {
            name: "Segundo".into(),
        })
        .unwrap();
    let state = first.profiles().unwrap();
    let second = state.profiles[1].id.parse().unwrap();
    first
        .edit_profile(ProfileEdit::Select { id: second })
        .unwrap();
    drop(first);
    let reopened = CatalogStore::open(&path).unwrap();
    assert_eq!(reopened.profiles().unwrap().active_id, second.to_string());
    assert!(reopened.progress(&target).unwrap().is_none());
    assert_eq!(
        reopened
            .for_profile(state.profiles[0].id.parse().unwrap())
            .unwrap()
            .backup()
            .unwrap()["deviceId"],
        saved["deviceId"]
    );
}

#[test]
fn reader_mode_round_trips_in_a_backup() {
    let (source, target) = store();
    assert_eq!(source.reader_mode(target.item_id, None).unwrap(), "paged");
    assert_eq!(
        source.reader_mode_with_default(target.item_id, None, "continuous").unwrap(),
        "continuous"
    );
    assert_eq!(source.reader_mode(target.item_id, None).unwrap(), "paged");
    source
        .reader_mode(target.item_id, Some("continuous"))
        .unwrap();
    let restored = CatalogStore::in_memory().unwrap();
    restored.restore_backup(&source.backup().unwrap()).unwrap();
    assert_eq!(
        restored.reader_mode(target.item_id, None).unwrap(),
        "continuous"
    );
    assert!(restored
        .reader_mode(target.item_id, Some("invalid"))
        .is_err());
}

#[test]
fn personal_list_membership_and_removal_survive_restore() {
    use umd_core::ListEdit;
    let (db, target) = store();
    db.edit_list(ListEdit::Create {
        name: "Pendientes".into(),
    })
    .unwrap();
    let id = db.personal_lists().unwrap()[0]["id"]
        .as_str()
        .unwrap()
        .parse()
        .unwrap();
    db.edit_list(ListEdit::Member {
        id,
        item_id: target.item_id,
        enabled: true,
    })
    .unwrap();
    let other = CatalogStore::in_memory().unwrap();
    other.restore_backup(&db.backup().unwrap()).unwrap();
    assert_eq!(
        other.personal_lists().unwrap()[0]["items"][0]["itemId"],
        target.item_id.to_string()
    );
    other
        .edit_list(ListEdit::Member {
            id,
            item_id: target.item_id,
            enabled: false,
        })
        .unwrap();
    other
        .edit_list(ListEdit::Rename {
            id,
            name: "Terminados".into(),
        })
        .unwrap();
    assert_eq!(
        other.personal_lists().unwrap()[0]["itemStates"][0]["isMember"],
        false
    );
    other.edit_list(ListEdit::Delete { id }).unwrap();
    assert!(other.personal_lists().unwrap().is_empty());
    assert_eq!(other.backup().unwrap()["lists"][0]["isDeleted"], true);
}

#[test]
fn reopening_keeps_confirmed_revision_and_local_edits_scoped_to_this_library() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("library.db");
    let (source, target) = store();
    let account = uuid::Uuid::new_v4();
    let other = uuid::Uuid::new_v4();
    let origin = "https://example.test/umd/";
    source.set_favorite(target.item_id, true).unwrap();
    let doc = source.backup().unwrap();
    {
        let library = CatalogStore::open(&path).unwrap();
        library
            .restore_account_backup(&doc, origin, account, 4)
            .unwrap();
        library.remember_upload(origin, account, 5).unwrap();
        library
            .save_progress(&target, 42.0, "seconds", false)
            .unwrap();
    }
    let library = CatalogStore::open(&path).unwrap();
    assert_eq!(library.backup_revision(origin, account).unwrap(), Some(5));
    assert_eq!(
        library.progress(&target).unwrap().unwrap()["position"],
        42.0
    );
    assert_eq!(library.backup_revision(origin, other).unwrap(), None);
    assert_eq!(
        library
            .backup_revision("https://other.test/", account)
            .unwrap(),
        None
    );
    let mut invalid = doc.clone();
    invalid["format"] = json!("unsupported");
    assert!(library
        .restore_account_backup(&invalid, origin, other, 9)
        .is_err());
    assert_eq!(library.backup_revision(origin, account).unwrap(), Some(5));
    library
        .restore_account_backup(&doc, origin, other, 9)
        .unwrap();
    assert_eq!(library.backup_revision(origin, account).unwrap(), None);
    assert_eq!(library.backup_revision(origin, other).unwrap(), Some(9));
}

#[test]
fn saving_or_reading_a_position_does_not_deserialize_unrelated_titles() {
    let (source, target) = store();
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("library.db");
    let library = CatalogStore::open(&path).unwrap();
    library.import(&source.export().unwrap(), false).unwrap();
    let db = rusqlite::Connection::open(&path).unwrap();
    // A deliberately unreadable, unrelated row detects full-library deserialization.
    db.execute(
        "INSERT INTO rust_catalog VALUES(?1,'other','movie','','invalid JSON')",
        [uuid::Uuid::new_v4().to_string()],
    )
    .unwrap();
    library
        .save_progress(&target, 37.5, "seconds", false)
        .unwrap();
    assert_eq!(
        library.progress(&target).unwrap().unwrap()["position"],
        37.5
    );
    library.set_favorite(target.item_id, true).unwrap();
}
fn store() -> (CatalogStore, ProgressTarget) {
    let store = CatalogStore::in_memory().unwrap();
    let doc=CatalogDocument::parse(json!({"format":"universal-media-distribution.catalog","version":1,"items":[{"mediaType":"movie","subtype":"movie","name":"Film"}]}).to_string().as_bytes()).unwrap();
    let id = doc.items[0].id().unwrap();
    store.import(&doc, false).unwrap();
    (
        store,
        ProgressTarget {
            item_id: id,
            unit_kind: None,
            display_number: None,
            season_number: None,
        },
    )
}
#[test]
fn restores_favorites_progress_and_preserves_opaque_list_metadata() {
    let (original, target) = store();
    original.set_favorite(target.item_id, true).unwrap();
    original
        .save_progress(&target, 123.5, "seconds", false)
        .unwrap();
    original.set_favorite(target.item_id, false).unwrap();
    let mut doc = original.backup().unwrap();
    doc["lists"] = json!([{"id":"ab091674-dbbf-42ee-9d68-e158a0c8ad7c","name":"Mi lista","createdAt":umd_core::now(),"updatedAt":umd_core::now(),"items":[],"itemStates":[],"isDeleted":false,"syncVersion":doc["favoriteItemStates"][0]["syncVersion"]}]);
    let second = CatalogStore::in_memory().unwrap();
    second.restore_backup(&doc).unwrap();
    assert_eq!(
        second.progress(&target).unwrap().unwrap()["position"],
        123.5
    );
    assert!(second
        .page(&Default::default())
        .unwrap()
        .favorite_ids
        .is_empty());
    let exported = second.backup().unwrap();
    assert_eq!(exported["lists"], doc["lists"]);
    assert_ne!(exported["deviceId"], doc["deviceId"]);
    assert_eq!(exported["favoriteItemStates"][0]["isFavorite"], false);
    second.set_favorite(target.item_id, true).unwrap();
    let updated = second.backup().unwrap();
    assert!(
        updated["favoriteItemStates"][0]["syncVersion"]["clock"]
            .as_i64()
            .unwrap()
            > doc["favoriteItemStates"][0]["syncVersion"]["clock"]
                .as_i64()
                .unwrap()
    );
}
#[test]
fn malformed_restore_and_invalid_progress_leave_library_intact() {
    let (store, target) = store();
    store.set_favorite(target.item_id, true).unwrap();
    let before = store.backup().unwrap();
    let mut malformed = before.clone();
    malformed["favoriteItemStates"][0]["item"]["itemId"] = json!(uuid::Uuid::new_v4());
    assert!(store.restore_backup(&malformed).is_err());
    assert_eq!(store.backup().unwrap()["catalog"], before["catalog"]);
    assert!(store
        .save_progress(&target, f64::NAN, "seconds", false)
        .is_err());
    assert!(store
        .save_progress(&target, -1.0, "seconds", false)
        .is_err());
    assert!(store.progress(&target).unwrap().is_none());
    malformed = before.clone();
    malformed["version"] = json!(5);
    assert!(validate_backup(&malformed).is_err());
}
#[test]
fn progress_survives_title_rename_and_restart() {
    let directory = tempfile::tempdir().unwrap();
    let path = directory.path().join("library.db");
    let (original, target) = store();
    original
        .save_progress(&target, 12.0, "seconds", true)
        .unwrap();
    let backup = original.backup().unwrap();
    {
        let disk = CatalogStore::open(&path).unwrap();
        disk.restore_backup(&backup).unwrap();
        let mut catalog = disk.export().unwrap();
        catalog.items[0].name = "Renamed".into();
        disk.import(&catalog, false).unwrap();
    }
    let disk = CatalogStore::open(path).unwrap();
    assert_eq!(
        disk.progress(&target).unwrap().unwrap()["isCompleted"],
        true
    );
    disk.save_progress(&target, 5.0, "seconds", false).unwrap();
    assert_eq!(
        disk.backup().unwrap()["progress"].as_array().unwrap().len(),
        1
    );
}
#[test]
fn old_missing_collections_are_initialized_and_device_identity_is_stable() {
    let (store, _) = store();
    let doc = store.backup().unwrap();
    assert_eq!(store.backup().unwrap()["deviceId"], doc["deviceId"]);
    let mut old = doc.clone();
    old["version"] = json!(1);
    for key in ["progress", "favoriteItemStates", "lists"] {
        old.as_object_mut().unwrap().remove(key);
    }
    store.restore_backup(&old).unwrap();
    let next: Value = store.backup().unwrap();
    assert!(next["progress"].is_array());
}

#[test]
fn independently_created_editions_merge_chapters_redirect_favorites_and_converge() {
    let (local, target) = store();
    local.set_favorite(target.item_id, true).unwrap();
    let mut remote = local.backup().unwrap();
    let other = uuid::Uuid::from_u128(7);
    remote["catalog"]["items"][0]["syncId"] = json!(other);
    remote["catalog"]["items"][0]["units"] =
        json!([{"kind":"chapter","displayNumber":"345","sortOrder":344}]);
    remote["favoriteItemStates"] = json!([]);
    let (changed, out) = local.reconcile_profile(Some(&remote)).unwrap();
    assert!(changed);
    let out = out.unwrap();
    assert_eq!(out["catalog"]["items"].as_array().unwrap().len(), 1);
    assert_eq!(out["catalog"]["items"][0]["syncId"], json!(other));
    assert_eq!(out["catalogRedirects"][0]["fromId"], json!(target.item_id));
    assert_eq!(out["favoriteItemStates"][0]["item"]["itemId"], json!(other));
    assert_eq!(local.export().unwrap().items.len(), 1);
    assert_eq!(local.reconcile_profile(Some(&out)).unwrap(), (false, None));
}

#[test]
fn catalog_tombstones_prevent_resurrection_and_backup_prunes_dangling_list_refs() {
    use umd_core::ListEdit;
    let (local, target) = store();
    local
        .edit_list(ListEdit::Create {
            name: "Favoritas".into(),
        })
        .unwrap();
    let list_id: uuid::Uuid = local.personal_lists().unwrap()[0]["id"]
        .as_str()
        .unwrap()
        .parse()
        .unwrap();
    local
        .edit_list(ListEdit::Member {
            id: list_id,
            item_id: target.item_id,
            enabled: true,
        })
        .unwrap();
    local
        .edit_list(ListEdit::Create {
            name: "Borrada".into(),
        })
        .unwrap();
    let deleted_list_id: uuid::Uuid = local.personal_lists().unwrap()[1]["id"]
        .as_str()
        .unwrap()
        .parse()
        .unwrap();
    local
        .edit_list(ListEdit::Delete {
            id: deleted_list_id,
        })
        .unwrap();
    let remote = local.backup().unwrap();

    // Delete the title locally using a tombstone import.
    let mut tomb = local.export().unwrap().items.remove(0);
    tomb.deleted = true;
    tomb.sync_id = Some(target.item_id);
    tomb.metadata.insert(
        "syncVersion".into(),
        json!({
            "clock": 50,
            "deviceId": remote["deviceId"],
            "operationId": uuid::Uuid::new_v4(),
            "modifiedAt": umd_core::now()
        }),
    );
    local
        .import(&CatalogDocument::new(vec![tomb.clone()]), false)
        .unwrap();
    assert!(local.export().unwrap().items.is_empty());

    // backup() must prune dangling list items and itemStates while keeping deleted lists valid.
    let marker_before = local.sync_marker().unwrap();
    let snap = local.backup().unwrap();
    let marker_after = local.sync_marker().unwrap();
    assert_eq!(
        marker_before.0, marker_after.0,
        "backup() must not write when deviceId already exists"
    );
    let active_list = snap["lists"]
        .as_array()
        .unwrap()
        .iter()
        .find(|l| l["id"] == list_id.to_string())
        .unwrap();
    assert!(active_list["items"].as_array().unwrap().is_empty());
    assert!(active_list["itemStates"].as_array().unwrap().is_empty());
    assert!(snap["lists"]
        .as_array()
        .unwrap()
        .iter()
        .any(|l| l["id"] == deleted_list_id.to_string() && l["isDeleted"] == true));

    // Reconciling with a stale remote must NOT resurrect the deleted title or error.
    let (_, outgoing) = local.reconcile_profile(Some(&remote)).unwrap();
    let outgoing = outgoing.unwrap();
    assert_eq!(outgoing["catalog"]["items"].as_array().unwrap().len(), 1);
    assert_eq!(outgoing["catalog"]["items"][0]["deleted"], true);
    assert!(local.export().unwrap().items.is_empty());
    assert_eq!(
        local.reconcile_profile(Some(&outgoing)).unwrap(),
        (false, None)
    );

    // Re-importing as live clears the tombstone; replace=true also clears tombstones.
    tomb.deleted = false;
    local
        .import(&CatalogDocument::new(vec![tomb]), false)
        .unwrap();
    assert_eq!(local.export().unwrap().items.len(), 1);
}

#[test]
fn validate_personal_follows_redirects_allows_favorite_tombstones_and_rejects_bad_collections() {
    let (local, target) = store();
    local.set_favorite(target.item_id, true).unwrap();
    let base = local.backup().unwrap();
    let version = base["favoriteItemStates"][0]["syncVersion"].clone();

    // Redirected itemId in progress/favorite/list must pass validate_backup.
    let old_id = uuid::Uuid::new_v4();
    let mut redirected = base.clone();
    redirected["catalogRedirects"] = json!([{
        "fromId": old_id,
        "toId": target.item_id,
        "syncVersion": version
    }]);
    redirected["favoriteItemStates"][0]["item"]["itemId"] = json!(old_id);
    validate_backup(&redirected).unwrap();

    // Circular redirect fails validation.
    let mut circular = redirected.clone();
    circular["catalogRedirects"]
        .as_array_mut()
        .unwrap()
        .push(json!({
            "fromId": target.item_id,
            "toId": old_id,
            "syncVersion": version
        }));
    assert!(validate_backup(&circular).is_err());

    // Self-redirect fails validation.
    let mut self_redir = base.clone();
    self_redir["catalogRedirects"] = json!([{
        "fromId": target.item_id,
        "toId": target.item_id,
        "syncVersion": version
    }]);
    assert!(validate_backup(&self_redir).is_err());

    // favoriteItemStates tombstone (isFavorite=false) for absent item is valid and reconciles cleanly.
    let absent_id = uuid::Uuid::new_v4();
    let mut fav_tomb = base.clone();
    fav_tomb["favoriteItemStates"] = json!([{
        "item": {"itemId": absent_id, "name": "Ausente", "mediaType": "movie", "subtype": "movie"},
        "isFavorite": false,
        "syncVersion": version
    }]);
    validate_backup(&fav_tomb).unwrap();
    assert!(local.reconcile_profile(Some(&fav_tomb)).is_ok());

    // Empty / duplicate favoriteStates fail validation.
    let mut bad_fav_state = base.clone();
    bad_fav_state["favoriteStates"] = json!([{
        "contributorKey": "   ",
        "isFavorite": true,
        "syncVersion": version
    }]);
    assert!(validate_backup(&bad_fav_state).is_err());
    bad_fav_state["favoriteStates"] = json!([
        {"contributorKey": "studio-a", "isFavorite": true, "syncVersion": version},
        {"contributorKey": "studio-a", "isFavorite": false, "syncVersion": version}
    ]);
    assert!(validate_backup(&bad_fav_state).is_err());

    // Duplicate list IDs and dangling list item references fail validation.
    let list_id = uuid::Uuid::new_v4();
    let mut dup_lists = base.clone();
    dup_lists["lists"] = json!([
        {"id": list_id, "name": "A", "createdAt": umd_core::now(), "updatedAt": umd_core::now(), "isDeleted": false, "items": [], "itemStates": [], "syncVersion": version},
        {"id": list_id, "name": "B", "createdAt": umd_core::now(), "updatedAt": umd_core::now(), "isDeleted": false, "items": [], "itemStates": [], "syncVersion": version}
    ]);
    assert!(validate_backup(&dup_lists).is_err());

    let mut bad_list_item = base.clone();
    bad_list_item["lists"] = json!([{
        "id": list_id,
        "name": "A",
        "createdAt": umd_core::now(),
        "updatedAt": umd_core::now(),
        "isDeleted": false,
        "items": [{"itemId": absent_id, "name": "Missing", "mediaType": "movie", "subtype": "movie"}],
        "itemStates": [],
        "syncVersion": version
    }]);
    assert!(validate_backup(&bad_list_item).is_err());
}

#[test]
fn list_constraints_and_deterministic_cross_device_name_deduplication() {
    use umd_core::ListEdit;
    let (phone, _) = store();
    // 81 chars rejected, 80 chars accepted.
    assert!(phone
        .edit_list(ListEdit::Create {
            name: "a".repeat(81),
        })
        .is_err());
    let long_name = "x".repeat(80);
    phone
        .edit_list(ListEdit::Create {
            name: long_name.clone(),
        })
        .unwrap();
    // Case/accent-insensitive duplicate rejected on Create & Rename.
    phone
        .edit_list(ListEdit::Create {
            name: "Películas".into(),
        })
        .unwrap();
    assert!(phone
        .edit_list(ListEdit::Create {
            name: " peliculas ".into(),
        })
        .is_err());
    let long_id: uuid::Uuid = phone.personal_lists().unwrap()[0]["id"]
        .as_str()
        .unwrap()
        .parse()
        .unwrap();
    assert!(phone
        .edit_list(ListEdit::Rename {
            id: long_id,
            name: "PELICULAS".into(),
        })
        .is_err());

    // Two devices create colliding 80-char lists -> deterministic deduplication <= 80 chars.
    let (desktop, _) = store();
    desktop
        .edit_list(ListEdit::Create {
            name: long_name.clone(),
        })
        .unwrap();
    let (_, phone_doc) = phone.reconcile_profile(None).unwrap();
    let (_, merged) = desktop.reconcile_profile(phone_doc.as_ref()).unwrap();
    let merged = merged.unwrap();
    let names: Vec<String> = merged["lists"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|l| l["isDeleted"] != true)
        .map(|l| l["name"].as_str().unwrap().to_owned())
        .collect();
    assert!(names.contains(&long_name));
    let suffixed = names.iter().find(|n| n.ends_with(" (2)")).unwrap();
    assert!(suffixed.chars().count() <= 80);
    assert_eq!(
        desktop.reconcile_profile(Some(&merged)).unwrap(),
        (false, None)
    );

    // Cap active lists at 100.
    let (capped, _) = store();
    for i in 0..100 {
        capped
            .edit_list(ListEdit::Create {
                name: format!("Lista {i}"),
            })
            .unwrap();
    }
    assert!(capped
        .edit_list(ListEdit::Create {
            name: "Lista 100".into(),
        })
        .is_err());
}

#[test]
fn delta_preserves_source_label_and_falls_back_on_catalog_redirect_patches() {
    let (local, target) = store();
    local.set_favorite(target.item_id, true).unwrap();
    let base = local.backup().unwrap();
    let mut delta = umd_core::backup_patch(&base, &base).unwrap();
    delta["catalog"]["sourceLabel"] = Value::Null;
    let applied = umd_core::apply_received_changes(&base, &delta)
        .unwrap()
        .unwrap();
    assert_eq!(
        applied["catalog"]["sourceLabel"],
        base["catalog"]["sourceLabel"]
    );

    // Patch introducing a catalogRedirect for an item in merged catalog returns Ok(None).
    let version = base["favoriteItemStates"][0]["syncVersion"].clone();
    let mut patch = delta;
    patch["catalogRedirects"] = json!([{
        "fromId": target.item_id,
        "toId": uuid::Uuid::new_v4(),
        "syncVersion": version
    }]);
    assert!(umd_core::merge_backup_patch(&base, &patch)
        .unwrap()
        .is_none());
}

#[test]
fn reconcile_catalog_distinguishes_release_date_years_and_orders_single_sided_units() {
    let local = CatalogStore::in_memory().unwrap();
    let id_a = uuid::Uuid::from_u128(101);
    let id_b = uuid::Uuid::from_u128(102);
    let doc_a = CatalogDocument::parse(
        json!({
            "format": "universal-media-distribution.catalog",
            "version": 1,
            "items": [{
                "syncId": id_a,
                "mediaType": "movie",
                "subtype": "movie",
                "name": "Dune",
                "releaseDate": "1984-12-14"
            }]
        })
        .to_string()
        .as_bytes(),
    )
    .unwrap();
    local.import(&doc_a, false).unwrap();
    let mut remote = local.backup().unwrap();
    remote["catalog"]["items"] = json!([{
        "syncId": id_b,
        "mediaType": "movie",
        "subtype": "movie",
        "name": "Dune",
        "releaseDate": "2021-10-22"
    }]);
    let (_, merged) = local.reconcile_profile(Some(&remote)).unwrap();
    let merged = merged.unwrap();
    // Different releaseDate years must NOT merge/redirect.
    assert_eq!(merged["catalog"]["items"].as_array().unwrap().len(), 2);
    assert!(merged["catalogRedirects"].as_array().unwrap().is_empty());

    // Winner lacking units still gets ordered units copied from the other side.
    let mut unordered = local.backup().unwrap();
    unordered["catalog"]["items"][0]["units"] = json!([
        {"kind": "chapter", "displayNumber": "2", "sortOrder": 2},
        {"kind": "chapter", "displayNumber": "1", "sortOrder": 1}
    ]);
    let (_, merged_units) = local.reconcile_profile(Some(&unordered)).unwrap();
    let merged_units = merged_units.unwrap();
    let item_a = merged_units["catalog"]["items"]
        .as_array()
        .unwrap()
        .iter()
        .find(|i| i["syncId"] == id_a.to_string())
        .unwrap();
    assert_eq!(item_a["units"][0]["displayNumber"], "1");
    assert_eq!(item_a["units"][1]["displayNumber"], "2");
}

#[test]
fn home_resumes_only_unfinished_visible_titles() {
    let (library, target) = store();
    library
        .save_progress(&target, 65., "seconds", false)
        .unwrap();
    let resume = library.continue_reading_or_watching(true).unwrap().unwrap();
    assert_eq!(resume["item"]["name"], "Film");
    assert_eq!(resume["progress"]["position"], 65.);
    let mut catalog = library.export().unwrap();
    catalog.items[0]
        .metadata
        .insert("categories".into(), json!(["+18"]));
    library.import(&catalog, false).unwrap();
    assert!(library
        .continue_reading_or_watching(true)
        .unwrap()
        .is_none());
    assert!(library
        .continue_reading_or_watching(false)
        .unwrap()
        .is_some());
    library
        .save_progress(&target, 70., "seconds", true)
        .unwrap();
    assert!(library
        .continue_reading_or_watching(false)
        .unwrap()
        .is_none());
}

#[test]
fn item_progress_batch_and_delete_item_work_together() {
    let (library, target) = store();
    let ep1 = ProgressTarget {
        item_id: target.item_id,
        unit_kind: Some("episode".into()),
        display_number: Some("1".into()),
        season_number: Some(1),
    };
    let ep2 = ProgressTarget {
        item_id: target.item_id,
        unit_kind: Some("episode".into()),
        display_number: Some("2".into()),
        season_number: Some(1),
    };
    library
        .save_progress_batch(&[
            (ep1.clone(), 1200., "seconds".into(), true),
            (ep2.clone(), 340., "seconds".into(), false),
        ])
        .unwrap();
    let rows = library.item_progress(target.item_id).unwrap();
    assert_eq!(rows.len(), 2);
    let cont = library.continue_items(false, 10).unwrap();
    assert_eq!(cont.len(), 1);
    assert_eq!(cont[0]["progress"]["content"]["displayNumber"], "2");
    assert!(library.delete_item(target.item_id).unwrap());
    assert!(library.item(target.item_id).unwrap().is_none());
    assert!(!library.delete_item(target.item_id).unwrap());
}
