using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

/*
 BlockHp 

 Adds a toggleable overlay that marks damaged blocks near the local player
 with a colored X on an exposed face.

 Goal
 - J toggles overlay on/off.
 - Block damage/change events enqueue positions only; marker dictionaries are
   mutated in Update on the main thread.
 - Periodic sphere rescan catches pre-existing damage and removes markers
   outside Radius.
 - Only damaged, non-terrain, non-air, non-child blocks with MaxDamage > 0
   are marked.
 - Color is red -> green by remaining HP fraction, alpha 0.9.
 - Multiblock aware: the exposed part best facing the camera is selected.
 - Marker count is capped at MaxMarkers; farthest markers are dropped.

 Important
 - Entity.position/player.position is world-space.
 - Camera transform is render-space: world-space minus Origin.position.
 - World block coordinates are converted to render-space before face math.
 - Conversion subtracts Origin.position through double arithmetic to reduce
   float precision jitter far from world origin.
 - ChunkCluster.GetBlock returns Air when a chunk is temporarily unavailable.
   faceCache keeps the last valid face so X markers do not disappear for a
   frame during chunk load/unload movement.
 - GL vertices are emitted in render-space.
 - ZTest is LEqual, so opaque geometry in front occludes markers.
 - Event handlers are unsubscribed on toggle off, cluster change, and destroy.

 Integration points
 - IModApi.InitMod(Mod)
 - Harmony(string).PatchAll()
 - EntityPlayerLocal.Awake postfix
 - EntityPlayerLocal.playerCamera
 - MonoBehaviour.Awake/Update/OnPostRender/OnDestroy
 - GameManager.Instance.World
 - Origin.position
 - World.worldToBlockPos(Vector3)
 - World.toChunkXZ(int)
 - World.toBlockXZ(int)
 - World.ChunkCache
 - ChunkCluster.GetChunkSync(int, int)
 - ChunkCluster.OnBlockDamagedDelegates
 - ChunkCluster.OnBlockChangedDelegates
 - BlockValueRefType.Block
 - BlockValueRef.BlockPosition
 - BlockValue.isair/isTerrain/ischild/damage/Block
 - Block.MaxDamage
 - Block.IsSeeThrough
 - Block.CanBlocksReplaceOrGroundCover
 - Block.IsTerrainDecoration
 - Block.isMultiBlock
 - Block.multiBlockPos
 - Shader.Find("Hidden/Internal-Colored")
 - GL.Begin(GL.LINES), GL.Color, GL.Vertex, GL.End()
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

    internal const float MarkerAlpha = 0.9f;

    internal const float FaceOffset = 0.01f;
    internal const float XHalfSize = 0.48f;

    internal const float FaceNormalYThreshold = 0.99f;
    internal const float MinFaceDotToCamera = 0f;

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
        public Vector3 normal;
    }

    private static readonly Vector3i[] Directions = Vector3i.AllDirections;

    private readonly Dictionary<Vector3i, float> markers =
        new Dictionary<Vector3i, float>();

    private readonly Dictionary<Vector3i, FacePick> faceCache =
        new Dictionary<Vector3i, FacePick>();

    private readonly ConcurrentQueue<Vector3i> pending =
        new ConcurrentQueue<Vector3i>();

    private readonly List<KeyValuePair<Vector3i, int>> trimScratch =
        new List<KeyValuePair<Vector3i, int>>();

    private bool enabledOverlay;
    private EntityPlayerLocal player;
    private ChunkCluster cluster;
    private Material material;
    private float nextRescanTime;

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

        Sub(world);
        Drain(world);

        if (Time.time < nextRescanTime)
            return;

        nextRescanTime = Time.time + Config.RescanInterval;
        Rescan(world);
    }

    private void OnPostRender()
    {
        if (!enabledOverlay || markers.Count == 0 || material == null)
            return;

        World world = GameManager.Instance?.World;
        if (world == null)
            return;

        Camera camera = GetComponent<Camera>();
        if (camera == null)
            return;

        Vector3 origin = Origin.position;
        Vector3 camRender = camera.transform.position;
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);

        material.SetPass(0);

        GL.Begin(GL.LINES);
        try
        {
            foreach (KeyValuePair<Vector3i, float> marker in markers)
            {
                Vector3 blockCenterRender =
                    BlockMath.WorldToRender(marker.Key, origin);

                if (!IsInFrustum(blockCenterRender, planes))
                    continue;

                if (!TryGetFace(world, marker.Key, camRender, origin, out FacePick pick))
                    continue;

                Vector3 partCenterRender =
                    BlockMath.WorldToRender(pick.partPos, origin);

                Vector3 faceCenterRender =
                    partCenterRender
                    + pick.normal * (BlockMath.HalfBlock + Config.FaceOffset);

                Color color = Color.Lerp(Color.red, Color.green, marker.Value);
                color.a = Config.MarkerAlpha;
                GL.Color(color);

                DrawX(faceCenterRender, pick.normal);
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

        while (pending.TryDequeue(out _)) { }
    }

    private void RemoveMarker(Vector3i pos)
    {
        markers.Remove(pos);
        faceCache.Remove(pos);
    }

    private void Sub(World world)
    {
        ChunkCluster next = world.ChunkCache;
        if (next == cluster)
            return;

        Unsub();

        if (next == null)
            return;

        next.OnBlockDamagedDelegates += OnDamaged;
        next.OnBlockChangedDelegates += OnChanged;
        cluster = next;
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

    private void Drain(World world)
    {
        if (pending.IsEmpty)
            return;

        Vector3i center = World.worldToBlockPos(player.position);

        while (pending.TryDequeue(out Vector3i pos))
            UpdateMarker(world, pos, center);

        Trim(center);
    }

    private void UpdateMarker(World world, Vector3i pos, Vector3i center)
    {
        if (!InRadius(pos, center))
        {
            RemoveMarker(pos);
            return;
        }

        if (TryHp(world, pos, out float hpFraction))
            markers[pos] = hpFraction;
        else
            RemoveMarker(pos);
    }

    private void Rescan(World world)
    {
        markers.Clear();
        faceCache.Clear();

        Vector3i center = World.worldToBlockPos(player.position);
        int radius = Config.Radius;
        int radiusSq = radius * radius;

        IChunk currentChunk = null;

        for (int y = center.y - radius; y <= center.y + radius; y++)
        {
            if ((uint)y >= Config.WorldHeight)
                continue;

            int dy = y - center.y;

            for (int z = center.z - radius; z <= center.z + radius; z++)
            {
                int dz = z - center.z;
                int chunkZ = World.toChunkXZ(z);

                for (int x = center.x - radius; x <= center.x + radius; x++)
                {
                    int dx = x - center.x;

                    if (dx * dx + dy * dy + dz * dz > radiusSq)
                        continue;

                    int chunkX = World.toChunkXZ(x);

                    if (currentChunk == null
                        || currentChunk.X != chunkX
                        || currentChunk.Z != chunkZ)
                    {
                        currentChunk =
                            world.ChunkCache.GetChunkSync(chunkX, chunkZ);

                        if (currentChunk == null)
                            continue;
                    }

                    BlockValue blockValue = currentChunk.GetBlock(
                        World.toBlockXZ(x),
                        y,
                        World.toBlockXZ(z));

                    if (blockValue.isair
                        || blockValue.isTerrain
                        || blockValue.ischild)
                    {
                        continue;
                    }

                    int maxDamage = blockValue.Block.MaxDamage;
                    if (maxDamage <= 0 || blockValue.damage <= 0)
                        continue;

                    Vector3i pos = new Vector3i(x, y, z);

                    markers[pos] = Mathf.Clamp01(
                        (maxDamage - blockValue.damage) / (float)maxDamage);
                }
            }
        }

        Trim(center);
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
        Vector3i pos,
        Vector3 camRender,
        Vector3 origin,
        out FacePick pick)
    {
        if (TryPickFace(world, pos, camRender, origin, out pick))
        {
            faceCache[pos] = pick;
            return true;
        }

        return faceCache.TryGetValue(pos, out pick);
    }

    private static bool TryPickFace(
        World world,
        Vector3i pos,
        Vector3 camRender,
        Vector3 origin,
        out FacePick pick)
    {
        pick = default;

        BlockValue blockValue = GetBlock(world, pos);
        if (blockValue.isair)
            return false;

        Block block = blockValue.Block;
        bool isMultiBlock = block.isMultiBlock;
        int partCount = isMultiBlock ? block.multiBlockPos.Length : 1;

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

                if (!IsOpenFace(world, neighborPos))
                    continue;

                Vector3 normal = Directions[d];
                float dot = Vector3.Dot(normal, toCam);

                if (dot <= bestDot)
                    continue;

                bestDot = dot;
                pick.partPos = partPos;
                pick.normal = normal;
                found = true;
            }
        }

        return found;
    }

    private static bool IsOpenFace(World world, Vector3i neighborPos)
    {
        BlockValue neighborBlockValue = GetBlock(world, neighborPos);

        return neighborBlockValue.isair
            || neighborBlockValue.Block.IsSeeThrough(
                world,
                neighborPos,
                neighborBlockValue)
            || neighborBlockValue.Block.CanBlocksReplaceOrGroundCover()
            || neighborBlockValue.Block.IsTerrainDecoration;
    }

    private static bool TryHp(
        World world,
        Vector3i pos,
        out float hpFraction)
    {
        hpFraction = 0f;

        BlockValue blockValue = GetBlock(world, pos);
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

    private static bool IsInFrustum(
        Vector3 point,
        Plane[] planes,
        float margin = 0.6f)
    {
        for (int i = 0; i < planes.Length; i++)
        {
            if (Vector3.Dot(planes[i].normal, point) + planes[i].distance < -margin)
                return false;
        }

        return true;
    }

    private static BlockValue GetBlock(World world, Vector3i pos)
    {
        return world.GetBlock(pos.x, pos.y, pos.z);
    }

    private static int CompareDistanceDescending(
        KeyValuePair<Vector3i, int> a,
        KeyValuePair<Vector3i, int> b)
    {
        return b.Value.CompareTo(a.Value);
    }

    private static void DrawX(Vector3 faceCenter, Vector3 normal)
    {
        Vector3 u = Mathf.Abs(normal.y) > Config.FaceNormalYThreshold
            ? Vector3.forward
            : Vector3.up;

        Vector3 v = Vector3.Cross(normal, u).normalized;
        u = Vector3.Cross(v, normal).normalized;

        float halfSize = Config.XHalfSize;

        GL.Vertex(faceCenter - (u + v) * halfSize);
        GL.Vertex(faceCenter + (u + v) * halfSize);
        GL.Vertex(faceCenter - (u - v) * halfSize);
        GL.Vertex(faceCenter + (u - v) * halfSize);
    }
}