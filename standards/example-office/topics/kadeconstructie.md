# Quay walls (kadeconstructie)

> Template sheet: the office fills in its own values. Everything below is a question the chat
> needs answered before it models a quay wall with `lwall` / `profile` / `extrude` / `sweep`.
> Cite this sheet as `[bron: standaard kadeconstructie]`.

## Reference
- Height system and the level the sizes are measured from (name of the NAP level):
- Which side is the water side:
- The placement line (front face of the stem, a grid, the capstone edge):

## Wall body
- Total height or top level:
- Stem thickness at the top and at the bottom (batter):
- Foot width; foot thickness at the stem and at the back (slope):
- Toe / heel split:
- Segment (joint) length between dilatations:

## Capstone (deksteen)
- Width, height, overhang on the water side:
- Chamfer / rounding sizes:
- Material:
- Joint length:
- Separate element or part of the wall:

## Apron (betonschort)
- Width, thickness, slope:
- Connection to the wall:
- Material:

## Sheet piles (damwand)
- Library family to use:
- System width (pair):
- Top and toe level rules:
- Temporary or permanent:

## Foundation
- Piles under the foot: family, grid along / across, rake (placed with `place --along`):

## Materials
- Concrete class / material name per part:
- Finish materials:

## Phase
- Per element kind, which phase kind (existing, temporary cofferdam, new, demolish):

## Category and naming
- Revit category per part (Generic Models or Structural Foundations):
- Family / type naming pattern (`profiles.typeName` in pack.json):
- Required shared parameters (object code etc.) and who fills them:

## Standard sections
Optional ready profiles for the office's recurring capstones and wall types, with their source
document and chapter, e.g.:

```
profile Deksteen standaard --points 0,0 500,0 500,250 470,280 0,280     (source: <document> §<chapter>)
```
