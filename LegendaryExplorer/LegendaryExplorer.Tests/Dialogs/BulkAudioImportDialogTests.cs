using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using LegendaryExplorer.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Dialogs;

[TestClass]
public class BulkAudioImportDialogTests
{
    private const string EventsWorkUnitId = "{11111111-1111-1111-1111-111111111111}";
    private const string ActorMixerWorkUnitId = "{22222222-2222-2222-2222-222222222222}";

    [TestMethod]
    public void BuildsPerAudioAndSharedStopEvents()
    {
        var firstWav = @"C:\Audio\First.wav";
        var secondWav = @"C:\Audio\Second.wav";
        var document = BuildEventsXml(
            [firstWav, secondWav],
            generateGenderedEvents: false,
            createSharedStopEvent: true,
            perAudioStopEventFiles: new HashSet<string>([firstWav], StringComparer.OrdinalIgnoreCase));

        CollectionAssert.AreEqual(
            new[] { "First_Play", "First_Stop", "Second_Play", "Stop" },
            document.Descendants("Event").Select(GetName).ToArray());

        AssertStopEvent(document, "First_Stop", "First");
        AssertStopEvent(document, "Stop", "First", "Second");
        Assert.IsNull(document.Descendants("Event").SingleOrDefault(element => GetName(element) == "Second_Stop"));

        var sharedActionShortIds = GetEvent(document, "Stop")
            .Descendants("Action")
            .Select(action => action.Attribute("ShortID")?.Value)
            .ToArray();
        Assert.AreEqual(sharedActionShortIds.Length, sharedActionShortIds.Distinct().Count());
    }

    [TestMethod]
    public void PerAudioStopEventTargetsBothGeneratedGenderVariants()
    {
        var wavPath = @"C:\Audio\Voice.wav";
        var document = BuildEventsXml(
            [wavPath],
            generateGenderedEvents: true,
            createSharedStopEvent: false,
            perAudioStopEventFiles: new HashSet<string>([wavPath], StringComparer.OrdinalIgnoreCase));

        CollectionAssert.AreEqual(
            new[] { "Voice_m_Play", "Voice_f_Play", "Voice_Stop" },
            document.Descendants("Event").Select(GetName).ToArray());
        AssertStopEvent(document, "Voice_Stop", "Voice_m", "Voice_f");
        Assert.IsNull(document.Descendants("Event").SingleOrDefault(element => GetName(element) == "Stop"));
    }

    [TestMethod]
    public void DoesNotCreateStopEventsWhenOptionsAreDisabled()
    {
        var document = BuildEventsXml(
            [@"C:\Audio\Voice.wav"],
            generateGenderedEvents: false,
            createSharedStopEvent: false,
            perAudioStopEventFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        CollectionAssert.AreEqual(
            new[] { "Voice_Play" },
            document.Descendants("Event").Select(GetName).ToArray());
        Assert.IsFalse(document.Descendants("Property")
            .Any(property => property.Attribute("Name")?.Value == "ActionType"));
    }

    private static XDocument BuildEventsXml(List<string> wavFiles, bool generateGenderedEvents,
        bool createSharedStopEvent, HashSet<string> perAudioStopEventFiles)
    {
        var method = typeof(BulkAudioImportDialog).GetMethod(
            "BuildEventsXml",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);

        var xml = method.Invoke(null,
            [EventsWorkUnitId, ActorMixerWorkUnitId, wavFiles, generateGenderedEvents, createSharedStopEvent, perAudioStopEventFiles]) as string;
        Assert.IsNotNull(xml);
        return XDocument.Parse(xml);
    }

    private static void AssertStopEvent(XDocument document, string eventName, params string[] expectedTargets)
    {
        var stopEvent = GetEvent(document, eventName);
        var actions = stopEvent.Descendants("Action").ToList();
        CollectionAssert.AreEqual(
            expectedTargets,
            actions.Select(action => action.Descendants("ObjectRef").Single().Attribute("Name")?.Value).ToArray());

        foreach (var action in actions)
        {
            var actionType = action.Descendants("Property")
                .Single(property => property.Attribute("Name")?.Value == "ActionType");
            Assert.AreEqual("int16", actionType.Attribute("Type")?.Value);
            Assert.AreEqual("2", actionType.Attribute("Value")?.Value);
            Assert.AreEqual(ActorMixerWorkUnitId, action.Descendants("ObjectRef").Single().Attribute("WorkUnitID")?.Value);
        }
    }

    private static XElement GetEvent(XDocument document, string eventName) =>
        document.Descendants("Event").Single(element => GetName(element) == eventName);

    private static string GetName(XElement element) => element.Attribute("Name")?.Value;
}
