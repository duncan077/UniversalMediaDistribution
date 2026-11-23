CREATE TEMP VIEW personal_rows AS
SELECT p.profile_id,'progress' AS kind,
       t.record_key || CASE WHEN u.id IS NULL THEN '' ELSE ':' || u.unit_kind || ':' ||
           coalesce(s.season_number,'-') || ':' || u.display_number_key END AS key,
       p.updated_at AS stamp,json_object('content',json_object('item',json(t.identity),'unitKind',u.unit_kind,
           'displayNumber',u.display_number,'seasonNumber',s.season_number),'position',p.position,
           'positionKind',p.position_kind,'isCompleted',json(iif(p.is_completed,'true','false'))) AS payload
FROM playback_progress p LEFT JOIN content_units u ON u.id=p.content_unit_id LEFT JOIN seasons s ON s.id=u.season_id
JOIN identities t ON t.local_id=coalesce(p.catalog_item_id,u.catalog_item_id)
UNION ALL
SELECT p.profile_id,'reader_preference',t.record_key,p.updated_at,json_object('item',json(t.identity),'displayMode',p.display_mode)
FROM reader_preferences p JOIN identities t ON t.local_id=p.catalog_item_id
UNION ALL
SELECT p.profile_id,'favorite_item',t.record_key,p.added_at,json_object('item',json(t.identity),'isFavorite',json('true'))
FROM library_entries p JOIN identities t ON t.local_id=p.catalog_item_id WHERE p.is_favorite
UNION ALL
SELECT profile_id,'favorite',contributor_key,added_at,json_object('contributorKey',contributor_key,'isFavorite',json('true'))
FROM favorite_contributors
UNION ALL
SELECT profile_id,'list',lower(replace(id,'-','')),updated_at,json_object('id',lower(id),'name',name,'createdAt',created_at,
    'isDeleted',json('false'),'items',json_array(),'itemStates',json_array())
FROM user_media_lists
UNION ALL
SELECT l.profile_id,'list_item',lower(replace(l.id,'-','')) || '|' || t.record_key,m.added_at,
    json_object('item',json(t.identity),'isMember',json('true'))
FROM user_media_list_items m JOIN user_media_lists l ON l.id=m.list_id JOIN identities t ON t.local_id=m.catalog_item_id;

CREATE TEMP VIEW removed_rows AS
SELECT r.profile_id,r.record_kind AS kind,r.record_key AS key,r.modified_at AS stamp,
    CASE r.record_kind
    WHEN 'favorite' THEN json_object('contributorKey',r.record_key,'isFavorite',json('false'))
    WHEN 'favorite_item' THEN json_object('item',json(t.identity),'isFavorite',json('false'))
    WHEN 'list' THEN json_object('id',r.record_key,'name','Lista eliminada','createdAt',r.modified_at,
        'isDeleted',json('true'),'items',json_array(),'itemStates',json_array())
    WHEN 'list_item' THEN json_object('item',json(t.identity),'isMember',json('false'))
    END AS payload
FROM user_sync_records r LEFT JOIN identities t ON t.record_key=
    CASE WHEN r.record_kind='list_item' THEN substr(r.record_key, instr(r.record_key, '|') + 1) ELSE r.record_key END
WHERE r.is_deleted AND (r.record_kind IN ('favorite','list') OR
    (r.record_kind IN ('favorite_item','list_item') AND t.local_id IS NOT NULL));
