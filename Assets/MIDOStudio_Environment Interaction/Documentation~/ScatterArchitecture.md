# Scatter Architecture (Phase 2)

This package is organized as a shared scatter core plus per-surface features.

- Runtime/Scatter/Core: shared data model, hash, interfaces
- Runtime/Scatter/Rendering: shared GPU instancing renderer
- Runtime/Scatter/Surfaces:
  - Grass: profile + grass-only interaction/press implementation + grass chunks
  - Pebble/Sand/Water/Leaf: independent profile layer (no forced press dependency)
- Runtime/Interaction: interaction map baking and global bridge

Design rule:
- Put only cross-surface logic in `Core`/`Rendering`.
- Put surface-specific behavior (e.g. grass press) under each surface folder.