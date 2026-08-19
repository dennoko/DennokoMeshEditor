# Dennoko Mesh Editor User Manual

A non-destructive mesh editing tool designed for VRChat avatar modification.
Easily adjust clothing clipping and body shape by simply grabbing and moving vertices. Original mesh asset files are never modified.

## Requirements

- Unity 2022.3.22f1 or later
- VRChat SDK
- **NDMF (nadena.dev.ndmf)** — Required

---

## Quick Start

### 1. Add Component
Right-click the mesh you want to edit in the Hierarchy and select `dennokoworks > Dennoko Mesh Editor`.

### 2. Enter Edit Mode
Click the **"Edit"** button in the Inspector. (*Automatically enters edit mode when adding the component to a mesh via the context menu).

### 3. Edit
Grab vertices and drag them with the transform handle. While grabbing the handle, scroll the mouse wheel to adjust the brush radius.

### 4. Finish Editing
Press the `Esc` key, or click the **"Finish Editing"** button in the Inspector.

That's it!

---

## Detailed Guide

### 1. Attaching the Component

In the Hierarchy, right-click the GameObject with the target mesh (`SkinnedMeshRenderer` / `MeshRenderer`) and select `dennokoworks > Dennoko Mesh Editor` (or add it via `Add Component` in the Inspector).

- **When added via right-click on a GameObject with a Renderer:**
  The mesh is automatically registered as an edit target and **Edit Mode starts immediately**.
- **When added to a GameObject without a Renderer:**
  Only the component is added. Specify the desired Renderer using **"Add Target"** in the Inspector.

If you want to edit multiple meshes together, add Renderers from **"Add Target"** in the Inspector.
Registering clothing and the avatar body at the same time allows you to blend seams and borders seamlessly.

### 2. Editing

Click the **"Edit"** button in the Inspector to enter Edit Mode in the Scene View.

| Action | Result |
| --- | --- |
| Click a vertex on the mesh | Selects the closest **visible** vertex to the click position (Yellow dot = candidate, Blue circle = influence radius) |
| Drag the transform handle | Vertices within the radius move smoothly with falloff around the selected point |
| **Mouse wheel while grabbing handle** | **Changes influence radius (Scroll Up = Shrink, Scroll Down = Expand / Blender convention)** |
| `Esc` | Deselect current vertex. Press again to finish editing |
| Select another object | Finishes editing |

You can also adjust the radius, falloff type, and mirror settings directly in the on-screen overlay in the Scene View.

### About Vertex Selection

**Vertices hidden behind surfaces cannot be selected.** Vertices occluded by front-facing geometry are filtered out, and the closest visible vertex is selected.

Single-sided meshes (such as skirts or hair cards) can also be selected from the backside as long as they are visible on screen (occlusion is judged by geometric visibility, not normal orientation).

> **Note:** Only **Renderers registered as edit targets** are treated as occluders.
> For example, if only the body is registered and the clothing is not, you may select body vertices hidden under the clothing.
> Adding the clothing to the edit target list resolves this.

Once selected, **the influence radius includes back-facing vertices**. Moving thin cloth pieces moves both the front and back sides together as intended.

### Undo (Ctrl+Z)

**Each handle drag operation counts as a single Undo step.** Pressing `Ctrl+Z` undoes only the most recent drag.

Radius adjustments made with the mouse wheel while grabbing the handle are bundled into the same step, so moving and adjusting the radius reverts as a single action.

### Radius Adjustment during Handle Manipulation

Scrolling the mouse wheel while holding the handle expands or shrinks the influence radius.
**You can adjust the radius even before moving the handle.** You can decide the influence scope before starting the drag, or adjust it **while keeping the displacement applied**.
Results update in real-time on the mesh, allowing you to visually fine-tune the blend area.

The mouse wheel scales by a percentage per step, so adjustments are fine at small radii and broader at larger radii.
When shrinking the radius, vertices that fall outside the influence area smoothly return to their pre-drag positions.

The modified radius is confirmed when the mouse button is released and retained for subsequent edits.
Note that scrolling changes the radius **only while actively holding the handle**. When not holding the handle, scrolling zooms the Scene View as usual.

### 3. Upload Directly

No saving or export steps are necessary. Edits are stored on the component and applied non-destructively during the avatar build and upload process via NDMF.

---

## Brush Settings

### Radius

The influence radius in world units (meters). Displayed as a circle in the Scene View.

### Falloff

Controls how displacement smoothly decreases away from the center.

| Type | Characteristics |
| --- | --- |
| Smooth | Smooth falloff at edges. Default and recommended. |
| Linear | Constant linear falloff rate. |
| Sharp | Concentrated strong movement near center. |
| Constant | Uniform displacement throughout the radius (no falloff). |

---

## Mirror Editing

**Only operations performed while Mirror is enabled** are applied symmetrically.
It is not an operation that reapplies symmetry retrospectively to existing edits.

### Note

Edits made very close to the center axis do not trigger mirror reflection (to avoid overlapping radius operations from doubling displacement). Edits near the center line are treated as standard non-mirrored edits.

---

## Baking

You can export a mesh with the edits baked directly into a new asset file.

- Saved in the **same folder as the original mesh**.
- Named with the **`_edited`** suffix appended to the original mesh name (will not duplicate if already present).
- Incremental numbers are added if a file with the same name already exists (`Body_edited 1`, `Body_edited 2`...).

### Add as BlendShape

When enabled, baking outputs a mesh preserving the original shape while **adding the edits as a new BlendShape (shape key)**.

Checking this option reveals the **"BlendShape Name"** field where you can specify a custom shape key name.
If left empty, it defaults to the original mesh name with `_edited` (or incremental numbering).

### Note

Baking **only exports asset files**. It does not automatically replace Renderers in the active scene.
If you want to use the baked mesh, assign it manually to the Renderer (the baked asset will be highlighted in the Project window).

---

## Specifications & Limitations

### Supported

- Vertex translation (proportional editing)
- Mirror editing along arbitrary axes (X, Y, Z)
- Multi-mesh concurrent editing
- Real-time reflection in NDMF Preview
- Mesh and BlendShape baking

### Unsupported (By Design)

- Topology edits (adding/deleting vertices, faces, extruding)
- UV, Normal, or Bone Weight editing
- Box/Rectangle selection (click-to-select only)

### About Normals

**Normals and tangents are not recalculated.** While large deformations may cause lighting not to follow dramatic geometric changes, automatic recalculation would alter custom avatar shading, so this behavior is intentional.

### Vertex Index Dependency

Similar to BlendShapes, this tool **stores and applies deltas based on vertex indices of the original mesh**. Please keep the following in mind:

- **FBX Re-import / Re-export:**
  If the vertex count or vertex order changes after re-exporting from a 3D modeling tool (e.g. Blender), existing edit offsets will mismatch.
- **Replacing with a Different Mesh:**
  Assigning a completely different mesh to a Renderer with existing edit data will not transfer the edits correctly.
- **Safety Fallback for Mismatches:**
  If vertex count mismatches are detected due to mesh replacement or re-importing, **edit application is safely skipped** to prevent vertex distortion, and warnings are shown in the Inspector and Console.

### Compatibility with Other Tools

The tool edits against the mesh state provided by NDMF Preview.
During build, it runs in the Transforming phase, applying before tools that optimize or merge meshes (such as Avatar Optimizer / AAO), allowing seamless compatibility.

---

## Troubleshooting

### "NDMF preview not acquired" Warning Appears

NDMF Preview is either disabled or has not generated yet.
You can still edit in this state, but you will be editing the raw mesh **without the effects of other avatar tools**.

Check the Unity toolbar to ensure NDMF Preview is enabled.

### "Vertex count differs from when edited" Warning Appears

The original mesh was replaced or re-imported with changed vertex order.
In this state, edits are suspended to prevent distorting incorrect vertices.

Since edits rely on vertex indices, the edits will need to be redone.
Click "Clear All Edits" in the Inspector to reset.

### Edits Are Not Reflected

Please check:
- Is the component enabled?
- Are target Renderers assigned?
- Is NDMF Preview active?
