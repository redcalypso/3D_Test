# 00.00 Lab Package Layout

- Runtime/
  - Scatter/Core: common scatter data and contracts
  - Scatter/Rendering: shared renderer(s)
  - Scatter/Surfaces:
    - Grass: profiles + grass-only press logic + chunk assets
    - Pebble: profiles
    - Sand: profiles
    - Water: profiles
    - Leaf: profiles
  - Interaction: interaction map baker and runtime interaction bridge
  - Shaders: shared shader/compute assets
- Editor/
  - Tools: brush and migration tools
- Samples~/
  - Scenes: sample scenes
  - Assets: sample-only resources
- Documentation~/
  - architecture notes