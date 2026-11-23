-- Stage the shared pieces once. The source connection is read-only and these
-- temporary tables disappear when conversion finishes.
CREATE TEMP TABLE packages AS
SELECT p.id, json_object(
    'displayName',p.display_name,'language',p.language,'magnetUri',p.magnet_uri,
    'torrentMetadataBase64',umd_base64(p.torrent_metadata),'infoHashV1',p.info_hash_v1,
    'infoHashV2',p.info_hash_v2,'isPrivate',json(iif(p.is_private,'true','false')),
    'totalSize',p.total_size,'sourceUri',p.source_uri,
    'trackers',json((SELECT json_group_array(json_object(
        'name',e.name,'endpoint',e.endpoint_template,'transport',e.transport,'clientFamily',e.client_family,
        'requiresAuthentication',json(iif(e.requires_authentication,'true','false')),
        'credentialKey',e.credential_key,
        'tier',t.tier,'sortOrder',t.sort_order) ORDER BY t.tier,t.sort_order)
        FROM torrent_package_trackers t JOIN tracker_endpoints e ON e.id=t.tracker_endpoint_id
        WHERE t.torrent_package_id=p.id AND e.is_enabled))
) AS payload FROM torrent_packages p;
CREATE UNIQUE INDEX temp.packages_id ON packages(id);

CREATE TEMP TABLE sources AS
SELECT m.catalog_item_id,m.content_unit_id,m.sort_order,json_object(
    'assetRole',m.asset_role,'sourceLayout',m.source_layout,'fileIndex',m.file_index,
    'filePath',nullif(m.file_path,''),'mimeType',m.mime_type,'container',m.container,
    'videoQuality',m.video_quality,'language',m.language,'sizeBytes',m.size_bytes,
    'sortOrder',m.sort_order,'torrent',json(p.payload)) AS payload
FROM media_sources m JOIN packages p ON p.id=m.torrent_package_id;
CREATE INDEX temp.sources_item ON sources(catalog_item_id,sort_order);
CREATE INDEX temp.sources_unit ON sources(content_unit_id,sort_order);

CREATE TEMP TABLE units AS
SELECT u.catalog_item_id,u.season_id,u.sort_order,json_object(
    'kind',u.unit_kind,'displayNumber',u.display_number,'sortOrder',u.sort_order,'name',u.name,
    'thumbnailRef',u.thumbnail_ref,'durationSeconds',u.duration_seconds,'pageCount',u.page_count,
    'releaseDate',u.release_date,'isSpecial',json(iif(u.is_special,'true','false')),
    'sources',json((SELECT json_group_array(json(payload) ORDER BY sort_order) FROM sources WHERE content_unit_id=u.id))
) AS payload FROM content_units u;
CREATE INDEX temp.units_item ON units(catalog_item_id,season_id,sort_order);
CREATE INDEX temp.units_season ON units(season_id,sort_order);

CREATE TEMP TABLE rights AS
SELECT catalog_item_id,json_group_array(json_object('sourceName',source_name,'sourceUri',source_uri,
    'licenseName',license_name,'licenseUri',license_uri,'attribution',attribution,
    'rightsNote',rights_note,'verifiedAt',verified_at) ORDER BY source_name) AS payload
FROM distribution_rights GROUP BY catalog_item_id;
CREATE UNIQUE INDEX temp.rights_item ON rights(catalog_item_id);

CREATE TEMP VIEW catalog_export AS
SELECT i.id AS local_id,json_object(
    'syncId',i.sync_id,'name',i.name,'mediaType',i.media_type,'subtype',substr(i.subtype_code,instr(i.subtype_code,'.')+1),
    'originalName',i.original_name,'sortName',i.sort_name,'description',i.description,'releaseDate',i.release_date,
    'releaseYear',i.release_year,'endDate',i.end_date,'originalLanguage',i.original_language,'countryCode',i.country_code,
    'publicationStatus',CASE i.publication_status WHEN 1 THEN 'announced' WHEN 2 THEN 'ongoing' WHEN 3 THEN 'completed'
        WHEN 4 THEN 'cancelled' WHEN 5 THEN 'paused' ELSE 'unknown' END,
    'contentRating',i.content_rating,'runtimeSeconds',i.runtime_seconds,'pageCount',i.page_count,
    'thumbnailRef',i.thumbnail_ref,'backdropRef',i.backdrop_ref,
    'syncVersion',json(CASE WHEN i.sync_clock IS NOT NULL THEN json_object('clock',i.sync_clock,'deviceId',i.sync_device,
        'operationId',i.sync_operation,'modifiedAt',i.sync_modified) END),
    'genres',json((SELECT json_group_array(g.name ORDER BY g.name) FROM catalog_item_genres ig
        JOIN genres g ON g.id=ig.genre_id WHERE ig.catalog_item_id=i.id)),
    'categories',json((SELECT json_group_array(c.name ORDER BY c.name) FROM catalog_item_categories ic
        JOIN categories c ON c.id=ic.category_id WHERE ic.catalog_item_id=i.id)),
    'alternativeNames',json((SELECT json_group_array(json_object('name',name,'language',coalesce(nullif(language,''),'und'),
        'aliasType',alias_type) ORDER BY name) FROM catalog_alternative_names WHERE catalog_item_id=i.id)),
    'contributors',json((SELECT json_group_array(json_object('kind',c.kind,'name',c.name,'url',c.url,'thumbnailRef',c.thumbnail_ref,
        'credits',json((SELECT json_group_array(json_object('role',cc.role,'characterName',nullif(cc.character_name,''),
            'sortOrder',cc.sort_order) ORDER BY cc.sort_order) FROM catalog_item_credits cc
            WHERE cc.contributor_id=c.id AND cc.catalog_item_id=i.id))) ORDER BY c.name)
        FROM contributors c WHERE c.id IN (SELECT contributor_id FROM catalog_item_credits WHERE catalog_item_id=i.id))),
    'seasons',json((SELECT json_group_array(json_object('number',s.season_number,'displayName',s.display_name,
        'thumbnailRef',s.thumbnail_ref,'releaseDate',s.release_date,'sortOrder',s.sort_order,
        'units',json((SELECT json_group_array(json(payload) ORDER BY sort_order) FROM units WHERE season_id=s.id)))
        ORDER BY s.sort_order) FROM seasons s WHERE s.catalog_item_id=i.id)),
    'units',json((SELECT json_group_array(json(payload) ORDER BY sort_order) FROM units WHERE catalog_item_id=i.id AND season_id IS NULL)),
    'sources',json((SELECT json_group_array(json(payload) ORDER BY sort_order) FROM sources WHERE catalog_item_id=i.id)),
    'distributionRights',json(coalesce((SELECT payload FROM rights WHERE catalog_item_id=i.id),'[]'))
) AS payload FROM catalog_items i;
