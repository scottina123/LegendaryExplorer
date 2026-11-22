class SFXSeqCond_CheckCurrentMap extends SequenceCondition;

// Compare the active persistent map, rather than a streamed or preloaded package.
var() name MapName;

event function Activated()
{
    local WorldInfo CurrentWorld;
    local bool bMatches;

    CurrentWorld = class'WorldInfo'.static.GetWorldInfo();
    bMatches = CurrentWorld != None && MapName != 'None'
        && CurrentWorld.GetMapName() ~= string(MapName);
    OutputLinks[0].bHasImpulse = bMatches;
    OutputLinks[1].bHasImpulse = !bMatches;
}

defaultproperties
{
    MapName = 'None'
    InputLinks = ({LinkDesc = "In"})
    OutputLinks = ({Links = (), LinkDesc = "True"}, {Links = (), LinkDesc = "False"})
    VariableLinks = ()
}
