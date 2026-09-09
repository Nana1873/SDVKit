# RecipeProbe sample helper

This is the complete source of the disposable helper used for the observed
[crop-to-machine authoring recipe](../../../crop-machine-authoring.md). It is a
sample-specific setup and reporting aid, not part of SDVKit and not a
distributable mod.

Copy this whole directory into the lab's ignored `.sdvkit/` area before
building it. Select the copied build explicitly with `project review start
--companion`. The project disables automatic deployment and ZIP creation so a
build cannot write to a normal or mod-manager-owned `Mods` directory.

The helper may place, mature, advance, or reposition only its own marked probe
objects in the owned disposable test save. Those synthetic operations never
count as proof of the native watering, harvest, machine, menu-transfer, or
save/reload effects required by the recipe.
