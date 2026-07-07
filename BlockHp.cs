using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

/*
 BlockHp

 Goal
 -----
 J toggles an overlay that marks damaged blocks near the local player
 with a colored X on an exposed face
 Only damaged non terrain non air non child blocks with MaxDamage above
 zero are marked
 Color runs red to green by remaining HP fraction alpha 0.9
 Marker count is capped at MaxMarkers farthest markers are dropped first

 Why time-sliced scanning instead of a synchronous sphere scan
 --------------------------------------------------------------
 A periodic rescan catches pre existing damage and removes markers
 outside Radius The naive version is a triple loop over a radius 40
 sphere 81 by 81 columns equals 6561 columns in one frame which caused a
 multi millisecond frame spike
 Instead the scan is sliced across frames ScanColumnsPerFrame columns
 are processed per Update into a scratch dictionary scanScratch and the
 snapshot is atomically swapped into the live marker set on completion
 At 512 columns per frame a full scan completes in about 13 frames well
 under RescanInterval while the per frame cost stays bounded and small
 The vertical loop per column is limited to the exact sphere extent
 maxDy equals floor of sqrt of radius squared minus horizontal squared
 instead of testing every block in the full vertical range
 Chunk lookups are memoized per column run including the missing chunk
 case so a hole in the loaded area costs one GetChunkSync per chunk not
 one per block column inside it

 Why time-sliced scanning instead of a worker thread
 ----------------------------------------------------
 ChunkCluster and Chunk access is only guaranteed safe from the main
 thread the game itself takes per chunk locks in paths like
 GetIndexedBlocks A worker thread would require locking every chunk
 read and re validating results against concurrent unloads
 Slicing the loop across Updates keeps everything on the main thread
 the same thread safety model as a synchronous scan so no locks are
 needed and ChunkCluster.GetChunkSync remains safe to call

 Why event handlers only enqueue positions
 ------------------------------------------
 ChunkCluster.OnBlockDamagedDelegates and OnBlockChangedDelegates can
 fire off the main thread Handlers push positions into a
 ConcurrentQueue and nothing else All dictionary mutation happens in
 Update on the main thread
 Handler signatures match the delegates exactly
 OnBlockDamagedDelegate(BlockValueRef, BlockValue, int, int)
 OnBlockChangedDelegate(Vector3i, BlockValue, sbyte, TextureFullArray, BlockValue)

 Important scan consistency note
 --------------------------------
 While a scan is in progress live event updates are mirrored into
 scanScratch A marker removed by an event is also removed from the
 snapshot so the completion swap cannot resurrect it A block damaged in
 an already scanned region is written into the snapshot so it survives
 the swap
 FinishScan recomputes the trim center from the current player position
 instead of reusing the position captured at BeginScan so a fast moving
 player cannot cause the trim to drop markers that are already inside
 the current radius

 Important coordinate precision note
 ------------------------------------
 Entity position and player position are world space
 Camera transform is render space world space minus Origin.position
 World block coordinates are converted to render space before face math
 The conversion subtracts Origin.position through double arithmetic to
 reduce float precision jitter far from the world origin Origin
 repositions in 16m steps see Origin.DoReposition newOrigin.x equals
 int cast anded with -16
 The vertical bounds check matches the engine's own for example
 ChunkCluster.GetBlock(int,int,int) tests (uint)y < 256u

 Important chunk streaming note
 -------------------------------
 ChunkCluster.GetBlock returns Air when a chunk is temporarily
 unavailable faceCache keeps the last valid face per marker so X
 markers do not disappear for a frame during chunk load and unload
 movement faceCache is never wiped mid scan stale entries are pruned
 only after the time-sliced scan completes so the last valid face
 guarantee also holds across rescans

 Important cluster change note
 ------------------------------
 Event handlers are unsubscribed on toggle off cluster change and
 destroy A cluster change for example world reload or teleport to
 another cluster also aborts any in progress scan clears all markers
 the face cache and the pending queue then forces an immediate rescan
 because marker positions from the previous cluster are meaningless in
 the new one

 Important block access note
 ----------------------------
 GetBlock(Vector3i) is a default interface method on IBlockAccess it
 delegates to GetBlock(x, y, z) and is not declared on ChunkCluster
 itself so it cannot be called through a ChunkCluster typed variable
 All block reads therefore use the ChunkCluster.GetBlock(int, int, int)
 overload directly

 Rendering
 ----------
 The renderer component lives on the player camera GameObject so the
 Camera is resolved once in Awake instead of GetComponent per frame
 Frustum planes come from the non allocating
 GeometryUtility.CalculateFrustumPlanes(camera, planes) overload into a
 reused Plane array of six no per frame allocation
 FrustumCullMargin adds tolerance in render space units so markers on
 the very edge of the screen do not pop in and out
 All six face normals are axis aligned so the tangent basis for drawing
 the X never changes at runtime It is precomputed once in the static
 constructor removing two Cross and two normalizations per marker per
 frame from the hot render loop FacePick stores an index into the
 precomputed arrays instead of a normal vector
 The basis seed axis uses VerticalAxisDot as the normal.y threshold For
 axis aligned normals normal.y is strictly -1 0 or 1 so any threshold
 between 0 and 1 selects the same seed this is a structural constant
 not a behavioral one Vertical faces seed from world up top and bottom
 faces seed from forward
 GL vertices are emitted in render space ZTest is LEqual so opaque
 geometry in front occludes markers
 A block fully destroyed between the damage event and the HP read has
 already been replaced by Air or a downgrade the air terrain child
 checks handle both without throwing

 Multiblock handling
 --------------------
 The exposed part best facing the camera is selected across all parts
 MultiBlockArray.Get(int _idx, int _blockId, int _rotation) is called
 with BlockValue.rotation a 5 bit byte always a valid rotation input
 for BlockShape.GetRotation inside Get
 A multiblock with a null or empty part list degenerates to a single
 part at the parent position defensive Length should be at least 1 for
 any real multiblock see the Block.MultiBlockArray ctor
 Face openness uses Block.IsSeeThrough(WorldBase, Vector3i, BlockValue)
 World derives from WorldBase so passing it directly is exact plus
 CanBlocksReplaceOrGroundCover and IsTerrainDecoration

 What the mod does in order
 ---------------------------
 1 Patch EntityPlayerLocal.Awake
   Harmony Postfix Awake is public override void Awake publicized from
   protected via PublicizedFrom playerCamera is a public Camera field
   assigned inside Awake so the postfix always sees a valid camera
   The renderer component is added to the camera GameObject once and
   rebound via SetPlayer on every Awake

 2 Toggle with J
   Toggle on schedules an immediate rescan Toggle off unsubscribes and
   clears all state

 3 Subscribe to the current ChunkCluster
   Sub re subscribes when World.ChunkCache changes and resets all state
   on a cluster switch

 4 Drain the event queue
   Positions outside Radius are removed Others are re evaluated via
   TryHp and written into markers and mirrored into scanScratch while a
   scan is in progress

 5 Step the time-sliced scan
   ScanColumnsPerFrame columns per Update into scanScratch On the last
   column FinishScan swaps the snapshot into markers prunes stale
   faceCache entries trims against the current player position and
   schedules the next rescan

 6 Render in OnPostRender
   Frustum cull per marker pick the exposed face best facing the camera
   with the faceCache fallback then draw the X in render space

 Integration points for future migration
 -----------------------------------------
 IModApi.InitMod(Mod)
 Harmony(string).PatchAll()
 EntityPlayerLocal.Awake (public override, PublicizedFrom protected)
 EntityPlayerLocal.playerCamera (public Camera field)
 MonoBehaviour.Awake/Update/OnPostRender/OnDestroy
 GameManager.Instance.World
 Origin.position
 World.worldToBlockPos(Vector3)
 World.toChunkXZ(int)
 World.toBlockXZ(int)
 World.ChunkCache
 ChunkCluster.GetChunkSync(int, int)
 ChunkCluster.GetBlock(int, int, int)
 IBlockAccess.GetBlock(Vector3i) (default interface method, not used)
 ChunkCluster.OnBlockDamagedDelegates
 ChunkCluster.OnBlockChangedDelegates
 BlockValueRefType.Block
 BlockValueRef.BlockPosition
 BlockValue.isair/isTerrain/ischild/damage/rotation/type/Block
 Block.MaxDamage
 Block.IsSeeThrough(WorldBase, Vector3i, BlockValue)
 Block.CanBlocksReplaceOrGroundCover()
 Block.IsTerrainDecoration
 Block.isMultiBlock
 Block.multiBlockPos
 MultiBlockArray.Get(int, int, int)
 IChunk.GetBlock(int, int, int)
 Shader.Find("Hidden/Internal-Colored")
 GeometryUtility.CalculateFrustumPlanes(Camera, Plane[])
 GL.Begin(GL.LINES), GL.Color, GL.Vertex, GL.End()
*/

public class BlockHp : IModApi
{
    internal const string HarmonyId = "diqezit.blockhp";

    public void InitMod(Mod modInstance)
    {
        new Harmony(HarmonyId).PatchAll();
        Log.Out("[BlockHp] Loaded");
    }
}

internal static class Config
{
    internal const KeyCode ToggleKey = KeyCode.J;

    internal const int Radius = 40;
    internal const float RescanInterval = 3f;
    internal const int MaxMarkers = 1500;

    internal const int ScanColumnsPerFrame = 512;

    internal const float MarkerAlpha = 0.9f;

    internal const float FaceOffset = 0.01f;
    internal const float XHalfSize = 0.48f;

    internal const float MinFaceDotToCamera = 0f;

    internal const float FrustumCullMargin = 0.6f;

    internal const float VerticalAxisDot = 0.5f;

    internal const int BlendSrcAlpha = (int)BlendMode.SrcAlpha;
    internal const int BlendOneMinusSrcAlpha = (int)BlendMode.OneMinusSrcAlpha;
    internal const int CullOff = (int)CullMode.Off;
    internal const int ZWriteOff = 0;
    internal const int ZTestLessEqual = (int)CompareFunction.LessEqual;

    internal const uint WorldHeight = 256u;
}

internal static class BlockMath
{
    internal const float HalfBlock = 0.5f;

    internal static Vector3 WorldToRender(Vector3i worldPos, Vector3 origin)
    {
        return new Vector3(
            (float)((double)worldPos.x + HalfBlock - origin.x),
            (float)((double)worldPos.y + HalfBlock - origin.y),
            (float)((double)worldPos.z + HalfBlock - origin.z));
    }
}

[HarmonyPatch(typeof(EntityPlayerLocal), nameof(EntityPlayerLocal.Awake))]
public static class BlockHpPatch_EntityPlayerLocal_Awake
{
    private static void Postfix(EntityPlayerLocal __instance)
    {
        Camera camera = __instance.playerCamera;
        if (camera == null)
            return;

        BlockHpOverlayRenderer renderer =
            camera.GetComponent<BlockHpOverlayRenderer>()
            ?? camera.gameObject.AddComponent<BlockHpOverlayRenderer>();

        renderer.SetPlayer(__instance);
    }
}

public sealed class BlockHpOverlayRenderer : MonoBehaviour
{
    private struct FacePick
    {
        public Vector3i partPos;
        public int dirIndex;
    }

    private static readonly Vector3i[] Directions = Vector3i.AllDirections;

    private static readonly Vector3[] DirNormals;
    private static readonly Vector3[] DirTangentU;
    private static readonly Vector3[] DirTangentV;

    static BlockHpOverlayRenderer()
    {
        int count = Directions.Length;
        DirNormals = new Vector3[count];
        DirTangentU = new Vector3[count];
        DirTangentV = new Vector3[count];

        for (int i = 0; i < count; i++)
        {
            Vector3 normal = Directions[i];

            Vector3 seed = Mathf.Abs(normal.y) > Config.VerticalAxisDot
                ? Vector3.forward
                : Vector3.up;

            Vector3 v = Vector3.Cross(normal, seed).normalized;
            Vector3 u = Vector3.Cross(v, normal).normalized;

            DirNormals[i] = normal;
            DirTangentU[i] = u;
            DirTangentV[i] = v;
        }
    }

    private readonly Dictionary<Vector3i, float> markers =
        new Dictionary<Vector3i, float>();

    private readonly Dictionary<Vector3i, FacePick> faceCache =
        new Dictionary<Vector3i, FacePick>();

    private readonly ConcurrentQueue<Vector3i> pending =
        new ConcurrentQueue<Vector3i>();

    private readonly List<KeyValuePair<Vector3i, int>> trimScratch =
        new List<KeyValuePair<Vector3i, int>>();

    private readonly Dictionary<Vector3i, float> scanScratch =
        new Dictionary<Vector3i, float>();

    private readonly List<Vector3i> faceCachePruneScratch =
        new List<Vector3i>();

    private bool scanInProgress;
    private Vector3i scanCenter;
    private int scanColumnIndex;

    private bool enabledOverlay;
    private EntityPlayerLocal player;
    private ChunkCluster cluster;
    private Material material;
    private Camera cachedCamera;
    private float nextRescanTime;

    private readonly Plane[] frustumPlanes = new Plane[6];

    public void SetPlayer(EntityPlayerLocal localPlayer)
    {
        player = localPlayer;
    }

    private void Awake()
    {
        material = new Material(Shader.Find("Hidden/Internal-Colored"))
        {
            hideFlags = HideFlags.HideAndDontSave
        };

        material.SetInt("_SrcBlend", Config.BlendSrcAlpha);
        material.SetInt("_DstBlend", Config.BlendOneMinusSrcAlpha);
        material.SetInt("_Cull", Config.CullOff);
        material.SetInt("_ZWrite", Config.ZWriteOff);
        material.SetInt("_ZTest", Config.ZTestLessEqual);

        cachedCamera = GetComponent<Camera>();
    }

    private void Update()
    {
        if (Input.GetKeyDown(Config.ToggleKey))
            Toggle();

        if (!enabledOverlay)
            return;

        World world = GameManager.Instance?.World;
        if (world == null || player == null)
            return;

        ChunkCluster chunkCache = world.ChunkCache;
        if (chunkCache == null)
            return;

        Sub(chunkCache);
        Drain(chunkCache);

        if (scanInProgress)
        {
            StepScan(chunkCache);
            return;
        }

        if (Time.time < nextRescanTime)
            return;

        BeginScan();
    }

    private void OnPostRender()
    {
        if (!enabledOverlay || markers.Count == 0 || material == null)
            return;

        World world = GameManager.Instance?.World;
        ChunkCluster chunkCache = world?.ChunkCache;
        if (world == null || chunkCache == null)
            return;

        Camera camera = cachedCamera;
        if (camera == null)
            return;

        Vector3 origin = Origin.position;
        Vector3 camRender = camera.transform.position;
        GeometryUtility.CalculateFrustumPlanes(camera, frustumPlanes);

        material.SetPass(0);

        GL.Begin(GL.LINES);
        try
        {
            foreach (KeyValuePair<Vector3i, float> marker in markers)
            {
                Vector3 blockCenterRender =
                    BlockMath.WorldToRender(marker.Key, origin);

                if (!IsInFrustum(blockCenterRender, frustumPlanes))
                    continue;

                if (!TryGetFace(world, chunkCache, marker.Key, camRender, origin, out FacePick pick))
                    continue;

                Vector3 partCenterRender =
                    BlockMath.WorldToRender(pick.partPos, origin);

                Vector3 faceCenterRender =
                    partCenterRender
                    + DirNormals[pick.dirIndex]
                    * (BlockMath.HalfBlock + Config.FaceOffset);

                Color color = Color.Lerp(Color.red, Color.green, marker.Value);
                color.a = Config.MarkerAlpha;
                GL.Color(color);

                DrawX(faceCenterRender, pick.dirIndex);
            }
        }
        finally
        {
            GL.End();
        }
    }

    private void OnDestroy()
    {
        Unsub();

        if (material != null)
        {
            Destroy(material);
            material = null;
        }
    }

    private void Toggle()
    {
        enabledOverlay = !enabledOverlay;

        if (enabledOverlay)
            nextRescanTime = 0f;
        else
            ClearAll();

        Log.Out("[BlockHp] Overlay " + (enabledOverlay ? "ON" : "OFF"));
    }

    private void ClearAll()
    {
        Unsub();

        markers.Clear();
        faceCache.Clear();
        AbortScan();

        while (pending.TryDequeue(out _)) { }
    }

    private void AbortScan()
    {
        scanInProgress = false;
        scanColumnIndex = 0;
        scanScratch.Clear();
    }

    private void RemoveMarker(Vector3i pos)
    {
        markers.Remove(pos);
        faceCache.Remove(pos);

        if (scanInProgress)
            scanScratch.Remove(pos);
    }

    private void Sub(ChunkCluster next)
    {
        if (next == cluster)
            return;

        bool hadCluster = cluster != null;
        Unsub();

        next.OnBlockDamagedDelegates += OnDamaged;
        next.OnBlockChangedDelegates += OnChanged;
        cluster = next;

        if (hadCluster)
        {
            markers.Clear();
            faceCache.Clear();
            AbortScan();
            while (pending.TryDequeue(out _)) { }
            nextRescanTime = 0f;
        }
    }

    private void Unsub()
    {
        if (cluster == null)
            return;

        cluster.OnBlockDamagedDelegates -= OnDamaged;
        cluster.OnBlockChangedDelegates -= OnChanged;
        cluster = null;
    }

    private void OnDamaged(
        BlockValueRef blockValueRef,
        BlockValue blockValue,
        int damage,
        int attackerEntityId)
    {
        if (!enabledOverlay || blockValueRef.Type != BlockValueRefType.Block)
            return;

        pending.Enqueue(blockValueRef.BlockPosition);
    }

    private void OnChanged(
        Vector3i pos,
        BlockValue bvOld,
        sbyte densOld,
        TextureFullArray texOld,
        BlockValue bvNew)
    {
        if (!enabledOverlay)
            return;

        pending.Enqueue(pos);
    }

    private void Drain(ChunkCluster chunkCache)
    {
        if (pending.IsEmpty)
            return;

        Vector3i center = World.worldToBlockPos(player.position);

        while (pending.TryDequeue(out Vector3i pos))
            UpdateMarker(chunkCache, pos, center);

        Trim(center);
    }

    private void UpdateMarker(ChunkCluster chunkCache, Vector3i pos, Vector3i center)
    {
        if (!InRadius(pos, center))
        {
            RemoveMarker(pos);
            return;
        }

        if (TryHp(chunkCache, pos, out float hpFraction))
        {
            markers[pos] = hpFraction;

            if (scanInProgress)
                scanScratch[pos] = hpFraction;
        }
        else
        {
            RemoveMarker(pos);
        }
    }

    private void BeginScan()
    {
        scanCenter = World.worldToBlockPos(player.position);
        scanColumnIndex = 0;
        scanScratch.Clear();
        scanInProgress = true;
    }

    private void StepScan(ChunkCluster chunkCache)
    {
        int radius = Config.Radius;
        int radiusSq = radius * radius;
        int side = radius * 2 + 1;
        int columnCount = side * side;

        IChunk currentChunk = null;
        int cachedChunkX = int.MinValue;
        int cachedChunkZ = int.MinValue;

        int budget = Config.ScanColumnsPerFrame;

        while (budget-- > 0 && scanColumnIndex < columnCount)
        {
            int columnIndex = scanColumnIndex++;
            int dx = columnIndex % side - radius;
            int dz = columnIndex / side - radius;

            int horizontalSq = dx * dx + dz * dz;
            if (horizontalSq > radiusSq)
                continue;

            int x = scanCenter.x + dx;
            int z = scanCenter.z + dz;
            int chunkX = World.toChunkXZ(x);
            int chunkZ = World.toChunkXZ(z);

            if (chunkX != cachedChunkX || chunkZ != cachedChunkZ)
            {
                cachedChunkX = chunkX;
                cachedChunkZ = chunkZ;
                currentChunk = chunkCache.GetChunkSync(chunkX, chunkZ);
            }

            if (currentChunk == null)
                continue;

            int blockX = World.toBlockXZ(x);
            int blockZ = World.toBlockXZ(z);

            int maxDy = Mathf.FloorToInt(
                Mathf.Sqrt(radiusSq - horizontalSq));

            for (int dy = -maxDy; dy <= maxDy; dy++)
            {
                int y = scanCenter.y + dy;
                if ((uint)y >= Config.WorldHeight)
                    continue;

                BlockValue blockValue =
                    currentChunk.GetBlock(blockX, y, blockZ);

                if (blockValue.isair
                    || blockValue.isTerrain
                    || blockValue.ischild)
                {
                    continue;
                }

                int maxDamage = blockValue.Block.MaxDamage;
                if (maxDamage <= 0 || blockValue.damage <= 0)
                    continue;

                scanScratch[new Vector3i(x, y, z)] = Mathf.Clamp01(
                    (maxDamage - blockValue.damage) / (float)maxDamage);
            }
        }

        if (scanColumnIndex < columnCount)
            return;

        FinishScan();
    }

    private void FinishScan()
    {
        scanInProgress = false;

        markers.Clear();
        foreach (KeyValuePair<Vector3i, float> entry in scanScratch)
            markers[entry.Key] = entry.Value;
        scanScratch.Clear();

        faceCachePruneScratch.Clear();
        foreach (KeyValuePair<Vector3i, FacePick> entry in faceCache)
        {
            if (!markers.ContainsKey(entry.Key))
                faceCachePruneScratch.Add(entry.Key);
        }
        for (int i = 0; i < faceCachePruneScratch.Count; i++)
            faceCache.Remove(faceCachePruneScratch[i]);
        faceCachePruneScratch.Clear();

        Trim(World.worldToBlockPos(player.position));

        nextRescanTime = Time.time + Config.RescanInterval;
    }

    private void Trim(Vector3i center)
    {
        if (markers.Count <= Config.MaxMarkers)
            return;

        trimScratch.Clear();

        foreach (KeyValuePair<Vector3i, float> marker in markers)
        {
            int dx = marker.Key.x - center.x;
            int dy = marker.Key.y - center.y;
            int dz = marker.Key.z - center.z;

            trimScratch.Add(
                new KeyValuePair<Vector3i, int>(
                    marker.Key,
                    dx * dx + dy * dy + dz * dz));
        }

        trimScratch.Sort(CompareDistanceDescending);

        int toRemove = trimScratch.Count - Config.MaxMarkers;
        for (int i = 0; i < toRemove; i++)
            RemoveMarker(trimScratch[i].Key);

        trimScratch.Clear();
    }

    private bool TryGetFace(
        World world,
        ChunkCluster chunkCache,
        Vector3i pos,
        Vector3 camRender,
        Vector3 origin,
        out FacePick pick)
    {
        if (TryPickFace(world, chunkCache, pos, camRender, origin, out pick))
        {
            faceCache[pos] = pick;
            return true;
        }

        return faceCache.TryGetValue(pos, out pick);
    }

    private static bool TryPickFace(
        World world,
        ChunkCluster chunkCache,
        Vector3i pos,
        Vector3 camRender,
        Vector3 origin,
        out FacePick pick)
    {
        pick = default;

        BlockValue blockValue = chunkCache.GetBlock(pos.x, pos.y, pos.z);
        if (blockValue.isair)
            return false;

        Block block = blockValue.Block;
        bool isMultiBlock = block.isMultiBlock && block.multiBlockPos != null;
        int partCount = isMultiBlock ? block.multiBlockPos.Length : 1;

        if (partCount <= 0)
        {
            isMultiBlock = false;
            partCount = 1;
        }

        float bestDot = Config.MinFaceDotToCamera;
        bool found = false;

        for (int i = 0; i < partCount; i++)
        {
            Vector3i offset = isMultiBlock
                ? block.multiBlockPos.Get(i, blockValue.type, blockValue.rotation)
                : Vector3i.zero;

            Vector3i partPos = pos + offset;
            Vector3 partCenterRender = BlockMath.WorldToRender(partPos, origin);
            Vector3 toCam = (camRender - partCenterRender).normalized;

            for (int d = 0; d < Directions.Length; d++)
            {
                Vector3i neighborPos = partPos + Directions[d];

                if (!IsOpenFace(world, chunkCache, neighborPos))
                    continue;

                float dot = Vector3.Dot(DirNormals[d], toCam);

                if (dot <= bestDot)
                    continue;

                bestDot = dot;
                pick.partPos = partPos;
                pick.dirIndex = d;
                found = true;
            }
        }

        return found;
    }

    private static bool IsOpenFace(
        World world,
        ChunkCluster chunkCache,
        Vector3i neighborPos)
    {
        BlockValue neighborBlockValue = chunkCache.GetBlock(
            neighborPos.x, neighborPos.y, neighborPos.z);

        return neighborBlockValue.isair
            || neighborBlockValue.Block.IsSeeThrough(
                world,
                neighborPos,
                neighborBlockValue)
            || neighborBlockValue.Block.CanBlocksReplaceOrGroundCover()
            || neighborBlockValue.Block.IsTerrainDecoration;
    }

    private static bool TryHp(
        ChunkCluster chunkCache,
        Vector3i pos,
        out float hpFraction)
    {
        hpFraction = 0f;

        BlockValue blockValue = chunkCache.GetBlock(pos.x, pos.y, pos.z);
        if (blockValue.isair
            || blockValue.isTerrain
            || blockValue.ischild)
        {
            return false;
        }

        int maxDamage = blockValue.Block.MaxDamage;
        if (maxDamage <= 0 || blockValue.damage <= 0)
            return false;

        hpFraction = Mathf.Clamp01(
            (maxDamage - blockValue.damage) / (float)maxDamage);

        return true;
    }

    private static bool InRadius(Vector3i pos, Vector3i center)
    {
        int dx = pos.x - center.x;
        int dy = pos.y - center.y;
        int dz = pos.z - center.z;

        int radius = Config.Radius;
        return dx * dx + dy * dy + dz * dz <= radius * radius;
    }

    private static bool IsInFrustum(Vector3 point, Plane[] planes)
    {
        for (int i = 0; i < planes.Length; i++)
        {
            if (Vector3.Dot(planes[i].normal, point) + planes[i].distance
                < -Config.FrustumCullMargin)
            {
                return false;
            }
        }

        return true;
    }

    private static int CompareDistanceDescending(
        KeyValuePair<Vector3i, int> a,
        KeyValuePair<Vector3i, int> b)
    {
        return b.Value.CompareTo(a.Value);
    }

    private static void DrawX(Vector3 faceCenter, int dirIndex)
    {
        Vector3 u = DirTangentU[dirIndex];
        Vector3 v = DirTangentV[dirIndex];

        float halfSize = Config.XHalfSize;

        GL.Vertex(faceCenter - (u + v) * halfSize);
        GL.Vertex(faceCenter + (u + v) * halfSize);
        GL.Vertex(faceCenter - (u - v) * halfSize);
        GL.Vertex(faceCenter + (u - v) * halfSize);
    }
}
