using System;
using System.Buffers.Binary;
using System.IO;
using LegendaryExplorerCore.Scaleform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorerCore.Tests
{
    [TestClass]
    public class ScaleformActionTests
    {
        [TestMethod]
        public void RawOperandsAndUnknownOpcodesRoundTripWithoutTranscoding()
        {
            // Includes a legacy non-UTF8 string, a noncanonical NaN, a noncanonical boolean,
            // every standard Push operand type, an unknown long opcode, and End padding.
            byte[] original = Convert.FromHexString(
                "88060001006180FF00" +
                "962200" +
                "0080FF00" + "010100C07F" + "0203" + "04FF" + "057F" +
                "060000F87F01000000" + "07FFFFFFFF" + "0800" + "090000" +
                "FE0400DEADBEEF" + "7F" + "000000");
            string assembly = ScaleformActionScript.Disassemble(original);
            StringAssert.Contains(assembly, "FE Op_FE | DE AD BE EF");
            CollectionAssert.AreEqual(original, ScaleformActionScript.Assemble(assembly));
        }

        [TestMethod]
        public void SymbolicBranchesRelocateAfterStringEdits()
        {
            byte[] original = ScaleformActionScript.Assemble("""
                start: .pushstring "a"
                99 Jump -> done
                .pushstring "skipped"
                done: 9D If -> start
                00 End
                """);
            string disassembly = ScaleformActionScript.Disassemble(original);
            CollectionAssert.AreEqual(original, ScaleformActionScript.Assemble(disassembly));

            byte[] edited = ScaleformActionScript.Assemble("""
                start: .pushstring "a much longer string"
                99 Jump -> done
                .pushstring "this instruction also grew"
                done: 9D If -> start
                00 End
                """);
            int pushLength = 3 + BinaryPrimitives.ReadUInt16LittleEndian(edited.AsSpan(1));
            int forwardTarget = pushLength + 5 + BinaryPrimitives.ReadInt16LittleEndian(edited.AsSpan(pushLength + 3));
            Assert.AreEqual((byte)0x9D, edited[forwardTarget]);
            int backwardTarget = forwardTarget + 5 + BinaryPrimitives.ReadInt16LittleEndian(edited.AsSpan(forwardTarget + 3));
            Assert.AreEqual(0, backwardTarget);
        }

        [TestMethod]
        public void NestedContainersUseSizesOutsideTheActionHeaderLength()
        {
            // Function2 contains With; the Try holds three separately bounded streams.
            byte[] actions = ScaleformActionScript.Assemble("""
                9B DefineFunction | 66 00 00 00 01 00 | 3E
                8E DefineFunction2 | 00 00 00 01 00 00 06 00 | 94 02 00 01 00 07
                8F Try | 03 01 00 01 00 01 00 65 00 | 07 06 09
                00 End
                """);
            string assembly = ScaleformActionScript.Disassemble(actions);
            CollectionAssert.AreEqual(actions, ScaleformActionScript.Assemble(assembly));
            StringAssert.Contains(assembly, "94 02 00 01 00 07");
        }

        [TestMethod]
        public void StringsCanBeEditedWithoutManualHexEncoding()
        {
            byte[] actions = ScaleformActionScript.Assemble("""
                .constantpool "a;#b", "https://example.test", "é", "line\nbreak"
                .pushstring "she said \"hello\";#"
                End
                """);
            string assembly = ScaleformActionScript.Disassemble(actions);
            StringAssert.Contains(assembly, "61 3B 23 62 00");
            StringAssert.Contains(assembly, "C3 A9 00");
            CollectionAssert.AreEqual(actions, ScaleformActionScript.Assemble(assembly));
        }

        [TestMethod]
        public void EmptyStringAndConstantPoolAreValid()
        {
            byte[] actions = ScaleformActionScript.Assemble(".constantpool\n.pushstring \"\"\nEnd");
            CollectionAssert.AreEqual(Convert.FromHexString("8802000000960200000000"), actions);
        }

        [TestMethod]
        public void MalformedRecordLengthsAndOperandsAreRejected()
        {
            string[] malformed =
            [
                "", "07", "96020000", "9601000000", "9901000000", "88030002000000",
                "960200090000", "9601000A00", "9B0500000000010000", "94020002000700",
                "8E07000000000000000100", "8F0800000100000000000000", "0007"
            ];
            foreach (string hex in malformed)
            {
                byte[] bytes = Convert.FromHexString(hex);
                Assert.ThrowsExactly<InvalidDataException>(() => ScaleformActionScript.Validate(bytes), hex);
                Assert.ThrowsExactly<InvalidDataException>(() => ScaleformActionScript.Disassemble(bytes), hex);
            }
        }

        [TestMethod]
        public void BranchesCannotTargetOperandsOrEscapeTheirBlock()
        {
            Assert.ThrowsExactly<InvalidDataException>(() => ScaleformActionScript.Assemble("99 Jump | FC FF\nEnd"));
            Assert.ThrowsExactly<InvalidDataException>(() => ScaleformActionScript.Assemble("99 Jump | FF 7F\nEnd"));
            Assert.ThrowsExactly<InvalidDataException>(() => ScaleformActionScript.Assemble("99 Jump | FA FF\nEnd"));
            Assert.ThrowsExactly<InvalidDataException>(() => ScaleformActionScript.Assemble("94 With | 05 00 | 99 02 00 01 00\nEnd"));
        }

        [TestMethod]
        public void OuterBranchCannotEnterAFunctionBody()
        {
            Assert.ThrowsExactly<InvalidDataException>(() => ScaleformActionScript.Assemble("""
                99 Jump | 08 00
                9B DefineFunction | 00 00 00 01 00 | 3E
                End
                """));
        }

        [TestMethod]
        public void InvalidAssemblyReportsLineAndDoesNotProduceBytes()
        {
            var error = Assert.ThrowsExactly<InvalidDataException>(() => ScaleformActionScript.Assemble("End\n96 Stop | 00 00"));
            StringAssert.Contains(error.Message, "Assembly line 2");
            Assert.ThrowsExactly<InvalidDataException>(() => ScaleformActionScript.Assemble("99 Jump -> missing\nEnd"));
            Assert.ThrowsExactly<InvalidDataException>(() => ScaleformActionScript.Assemble("x: Play\nx: End"));
            Assert.ThrowsExactly<InvalidDataException>(() => ScaleformActionScript.Assemble(".pushstring \"x\\u0000y\"\nEnd"));
            Assert.ThrowsExactly<InvalidDataException>(() => ScaleformActionScript.Assemble("96 Push | 0\nEnd"));
        }
    }
}
