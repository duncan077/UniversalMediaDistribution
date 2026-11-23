CREATE TEMP VIEW docs AS SELECT key, parent, payload AS p FROM old.server_documents;

INSERT INTO accounts(id,username,display_name,salt,hash,created_at,iterations)
SELECT guid(p->>'accountId'),lower(p->>'username'),p->>'displayName',unbase64(p->>'passwordSalt'),
       unbase64(p->>'passwordHash'),p->>'createdAt',p->>'passwordIterations'
FROM docs WHERE parent='accounts/by-id';

INSERT INTO sessions SELECT lower(p->>'tokenHash'),guid(p->>'accountId'),unixepoch(p->>'expiresAt')
FROM docs WHERE key GLOB 'sessions/*' AND unixepoch(p->>'expiresAt')>unixepoch();

INSERT INTO snapshots SELECT guid(substr(parent,11)),p->>'revision',p->>'updatedAt',p->>'updatedByDeviceId',p->'document'
FROM docs WHERE key GLOB 'user-sync/*/latest.json';

INSERT INTO shared_lists SELECT p->>'shareId',guid(p->>'ownerAccountId'),guid(p->>'sourceListId'),p->>'name',
       json_array_length(p,'$.catalog.items'),p->>'createdAt',p->>'updatedAt' FROM docs WHERE parent='shared-lists/public';
INSERT INTO migrated_list_catalogs SELECT p->>'shareId',p->'catalog' FROM docs WHERE parent='shared-lists/public';

INSERT INTO friend_settings SELECT guid(substr(key,20,length(key)-24)),p->>'shareProgress',p->>'discoverableByUsername',
       coalesce(p->'hiddenItemKeys','[]') FROM docs WHERE parent='friends/visibility';

-- An accepted friendship requires both directed edges in the .NET store.
WITH edges AS (SELECT guid(p->>'accountId') AS owner,guid(p->>'friendAccountId') AS friend,
                      p->>'since' AS since,coalesce(p->>'shareProgress',0) AS shares FROM docs WHERE key GLOB 'friends/edges/*')
INSERT INTO friend_pairs SELECT a.owner,b.owner,a.owner,1,a.since,a.shares,b.shares
FROM edges a JOIN edges b ON b.owner=a.friend AND b.friend=a.owner WHERE a.owner<b.owner;

WITH pending AS (SELECT guid(p->>'fromAccountId') AS sender,guid(p->>'toAccountId') AS receiver,p->>'createdAt' AS sent
                 FROM docs WHERE key GLOB 'friends/requests/incoming/*')
INSERT INTO friend_pairs(a,b,sender,accepted,since)
SELECT min(sender,receiver),max(sender,receiver),sender,0,sent FROM pending WHERE true ON CONFLICT(a,b) DO NOTHING;

INSERT INTO friend_activity SELECT guid(p->>'accountId'),entry.value->>'itemKey',entry.value
FROM docs, json_each(p,'$.entries') entry WHERE docs.parent='friends/activity' AND coalesce(entry.value->>'hasProgress',1)=1
AND EXISTS(SELECT 1 FROM friend_settings s WHERE s.owner=guid(p->>'accountId') AND s.sharing AND NOT EXISTS(
    SELECT 1 FROM json_each(s.hidden) hidden WHERE hidden.value=entry.value->>'itemKey'));
