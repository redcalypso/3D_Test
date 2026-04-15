# EIS Grass Placement Workflow

This document explains the current team workflow for placing grass with the EIS scatter system.

Scope:
- Place grass data into a `ScatterField`
- Preview and edit it with the brush tool
- Render it at runtime with `ScatterRenderManager`
- Optionally hook it into EIS interaction

## 1. Core Concepts

The current system is split into 3 parts:

- `ScatterField`
  - Scene component that owns render settings and points to room data
- `RoomScatterDataSO`
  - Asset that stores all painted chunk data for one room/field
- `ScatterRenderManager`
  - Runtime renderer that discovers `ScatterField`s and draws all grass

Important change from the old workflow:
- We no longer create and manage many `ScatterChunkSO` assets by hand
- One room usually uses one `RoomScatterDataSO`
- That asset can contain many chunks internally

## 2. Minimum Setup

Create a GameObject for the room or grass root.

Add:
- `ScatterField`

Assign on `ScatterField`:
- `Room Data`
- `Variation Meshes`
- `Shared Material`

Required fields:
- `Variation Meshes`: grass mesh array used for per-cell variation
- `Shared Material`: material used by all instances in this field

If `Room Data` is empty:
1. Select the GameObject with `ScatterField`
2. In the inspector, press `Create Room Scatter Data`
3. Save the asset where the room data should live

Recommended naming:
- `{RoomName}_RoomScatterData.asset`

## 3. Render Manager Setup

Grass will not render in play mode unless there is an active `ScatterRenderManager`.

Use one manager per scene/session.

Recommended setup:
1. Add the prefab:
   - `Packages/com.midostudio.eis-system/Runtime/Prefabs/ScatterRenderManager.prefab`
2. Place it once in the scene
3. Leave `Auto Discover Fields` enabled unless there is a strong reason not to

Design rule:
- Do not place one manager per room
- Keep one central manager for the loaded scene

## 4. Brush Tool Workflow

Open:
- `Tools > MIDO > Scatter Brush Tool`

In the tool:
1. Set `Target Field`
2. Choose the `Surface Type`
3. Enable `Paint Mode`
4. Turn on `Create Chunk On First Paint` if you want chunks to be created automatically

Current input:
- `Shift + LMB`: paint
- `Ctrl + LMB`: erase
- `CapsLock + LMB`: scale

Important:
- With `Paint Mode` off, normal selection/move workflow should remain usable
- With `Paint Mode` on, the brush owns the SceneView interaction

## 5. Auto Chunk Creation

The current intended workflow is:
- Artist paints on the field
- If the painted position belongs to a chunk that does not exist yet, the tool creates that chunk inside the assigned `RoomScatterDataSO`
- Cell data is written immediately into that room asset

This means:
- No manual chunk asset creation per patch of grass
- Data stays grouped by room
- A single room asset can hold many chunks

## 6. Surface Projection

If the room is flat:
- You can paint directly on the field plane

If the room has steps, slopes, or static environment mesh:
- Enable collider-based surface hit in the brush tool
- Restrict hit targets with `Collider Layer Mask`

Recommended brush settings for projected placement:
- `Use Collider Surface Hit`: On
- `Collider Layer Mask`: terrain / floor / static environment layers only
- `Max Slope Deg`: set to the steepest valid floor angle
- `Fallback To Field Plane`: On only if you want painting to still work when no collider is hit

Recommended `ScatterField` settings for stored projection:
- `Project To Static Surface`: On
- `Projection Layer Mask`: floor/static geometry layers
- `Align To Surface Normal`: On only if the grass should lean with slope normals

Current behavior:
- Surface projection is sampled when data is created or rebaked
- The resulting local height/normal is stored in the cell data
- Runtime does not need to raycast every frame

## 7. Prefab / Editor Preview

For editing:
- The brush tool can preview the target field while editing
- This is editor-only behavior

For runtime:
- `ScatterRenderManager` is still the actual renderer

Important distinction:
- Brush preview exists to make authoring possible
- Runtime manager exists to make gameplay rendering work

If something is visible only when the runtime manager exists:
- That is runtime rendering, not authoring preview

## 8. Recommended Room Authoring Flow

For each room:
1. Create the room root GameObject or open the room prefab
2. Add `ScatterField`
3. Create and assign one `RoomScatterDataSO`
4. Assign `Variation Meshes`
5. Assign `Shared Material`
6. Open the brush tool
7. Paint grass
8. Let the tool auto-create new chunks as needed

Result:
- One room root in scene/prefab
- One `RoomScatterDataSO`
- Many chunks inside the room asset if needed

## 9. EIS Interaction Setup

Grass placement and grass interaction are separate.

To make painted grass react to EIS:
1. Add `InteractionMapBakerV2` to a scene object
2. Assign:
   - target transform
   - relaxation shader
   - stamp shader
3. Ensure the grass material/shader samples:
   - `_InteractionRT`
   - `_InteractionCamPosXZ`
   - `_InteractionCamParams`

Compatibility rule:
- EIS still publishes globals using the existing names above

Optional:
- Use `EISStampPreset` assets for stamp definitions
- Use `MMF_Feedback_EISStamp` when stamp requests should come from MMF

## 10. Team Checklist

Before handing a room off, verify:

- `ScatterField.roomData` is assigned
- `Variation Meshes` is not empty
- `Shared Material` is assigned
- Scene has exactly one `ScatterRenderManager`
- Brush tool can paint and erase
- Play mode renders the grass
- If interaction is needed:
  - `InteractionMapBakerV2` exists
  - grass shader reads EIS globals

## 11. Common Failure Cases

### Nothing renders in play mode

Check:
- `ScatterRenderManager` exists in the scene
- `ScatterField.sharedMaterial` is assigned
- `ScatterField.variationMeshes` has at least one mesh
- `RoomScatterDataSO` actually contains chunks/cells

### Brush paints nothing

Check:
- `Paint Mode` is enabled
- `Target Field` is assigned
- `RoomScatterDataSO` is assigned
- `Create Chunk On First Paint` is enabled, or the target chunk already exists
- Surface hit settings are not filtering out valid colliders

### Paint works only on flat plane

Check:
- `Use Collider Surface Hit` is enabled
- `Collider Layer Mask` includes the floor mesh layer
- `Max Slope Deg` is not too small

### Grass does not react to interaction

Check:
- `InteractionMapBakerV2` is running
- stamp requests are being issued
- the grass shader samples the EIS globals

## 12. Current Best Practices

- Use one `RoomScatterDataSO` per room
- Use one `ScatterRenderManager` per scene/session
- Keep `Variation Meshes` and material unified where possible
- Use projection only against valid floor/static layers
- Store projection at authoring time, not runtime
- Let the brush tool auto-create chunks instead of creating scatter data manually

## 13. Short Version

If someone only needs the practical version:

1. Add `ScatterField` to the room root
2. Create and assign `RoomScatterDataSO`
3. Assign grass meshes and material
4. Put one `ScatterRenderManager` in the scene
5. Open `Scatter Brush Tool`
6. Enable `Paint Mode`
7. Paint with `Shift + LMB`
8. Erase with `Ctrl + LMB`
9. Scale with `CapsLock + LMB`
10. Add `InteractionMapBakerV2` only if the grass must react to gameplay interaction
