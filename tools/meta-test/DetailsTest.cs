// Offline test for mod/src/SaveDetails.cs on a REAL decrypted run save (path given by run.sh), plus fixed cases.
using System;
using System.IO;
using TygMod;

internal static class DetailsTest
{
    public static int Run(string jsonPath, Action<bool, string> check)
    {
        // ---- fixed cases ----
        string mini =
            "{\"PlayerCharmInventoryData_count\":{\"__type\":\"int\",\"value\":2}," +
            "\"PlayerCharmInventoryData_id_0\":{\"__type\":\"string\",\"value\":\"A \\\"quoted\\\" \\u00e9\"}," +
            "\"PlayerCharmInventoryData_data_0\":{\"__type\":\"X\",\"value\":{\"Id\":\"nested\"}}," +
            "\"PlayerCharmInventoryData_id_1\":{\"__type\":\"string\",\"value\":\"B\"}," +
            "\"PlayerCharmInventoryData_id_2\":{\"__type\":\"string\",\"value\":\"left over\"}," +
            "\"BallMachineData_active_total\":{\"__type\":\"int\",\"value\":3}," +
            "\"BallMachineData_active_id_0\":{\"__type\":\"string\",\"value\":\"Ball\"}," +
            "\"BallMachineData_active_id_1\":{\"__type\":\"string\",\"value\":\"\"}," +
            "\"BallMachineData_active_id_2\":{\"__type\":\"string\",\"value\":\"Reaper Ball\"}," +
            "\"PlayerChipInventory_coin\":{\"__type\":\"int\",\"value\":7}," +
            "\"CharacterBodyPartsData_parts\":{\"__type\":\"Dict\",\"value\":{\"Left.Hand\":true,\"Right.R_Eye\":false,\"Left.PinkyTarget\":true}}}";
        SaveDetails m = SaveDetails.Parse(mini);
        check(m.Mementos.Count == 2 && m.Mementos[0] == "A \"quoted\" \u00e9" && m.Mementos[1] == "B", "mementos: count wins over left-over keys, escapes decoded");
        check(m.Balls.Count == 2 && m.Balls[0] == "Ball" && m.Balls[1] == "Reaper Ball", "balls: order kept, empty slot skipped");
        check(m.Tokens == 7 && m.BetLevel == -1, "tokens read, missing bet level = -1");
        check(m.CutParts.Count == 2 && m.CutParts[0] == "Left.Hand" && m.CutParts[1] == "Left.PinkyTarget", "cut parts = only true ones");
        check(SaveDetails.PartName("Left.Hand") == "left hand" && SaveDetails.PartName("Right.R_Eye") == "right eye" &&
              SaveDetails.PartName("Left.PinkyTarget") == "left pinky finger" && SaveDetails.PartName("Left.ThumbTrget") == "left thumb" &&
              SaveDetails.PartName("Left.Heart") == "heart", "part names");
        check(SaveDetails.Parse("").ItemCount == 0 && SaveDetails.Parse("not json").ItemCount == 0, "empty or bad input = no items");
        string coin = "{\"PlayerChipInventory_coin\":{\"__type\":\"double\",\"value\":2.5E+16}}";
        check(SaveDetails.Parse(coin).Tokens == 2.5E+16, "tokens saved as a double with an exponent");
        string huge = "{\"BallMachineData_active_total\":{\"__type\":\"int\",\"value\":2147483647}}";
        check(SaveDetails.Parse(huge).Balls.Count == 0, "huge count does not loop forever");

        // ---- real save ----
        if (jsonPath == null || !File.Exists(jsonPath))
        {
            Console.WriteLine("(no real save given: real-save checks skipped)");
            return 0;
        }
        SaveDetails d = SaveDetails.Parse(File.ReadAllText(jsonPath));
        Console.WriteLine("real save: mementos=[" + string.Join(", ", d.Mementos) + "]");
        Console.WriteLine("           balls=[" + string.Join(", ", d.Balls) + "]");
        Console.WriteLine("           ballSlots=[" + string.Join(", ", d.BallSlots) + "] consumables=[" + string.Join(", ", d.Consumables) +
                          "] starters=[" + string.Join(", ", d.Starters) + "] cut=[" + string.Join(", ", d.CutParts) + "] tokens=" + d.Tokens + " betLevel=" + d.BetLevel);
        // Expected values for sp-20261123-003541 (read by hand from that save on 2026-11-23).
        if (Path.GetFileName(jsonPath).StartsWith("sp-20261123-003541", StringComparison.Ordinal))
        {
            check(string.Join("|", d.Mementos) == "Double Watch|Ball Collection|decaying_bet_limit_01|grave_ledger_01|ThrashCan|Golden Disk",
                  "real: 6 mementos in slot order, left-over id_6 skipped");
            check(d.Balls.Count == 10 && d.Balls[0] == "Shopaholic Ball" && d.Balls[2] == "Token Ball" && d.Balls[9] == "Ying Yang Ball",
                  "real: 10 balls in machine order");
            check(string.Join("|", d.BallSlots) == "Void Meteor Ball|Void Meteor Ball|Ball", "real: ball slots");
            check(d.Consumables.Count == 0 && d.Starters.Count == 0, "real: no consumables, no starters (count 0)");
            check(d.CutParts.Count == 1 && d.CutParts[0] == "Left.Hand", "real: left hand cut");
        }
        return 0;
    }
}
