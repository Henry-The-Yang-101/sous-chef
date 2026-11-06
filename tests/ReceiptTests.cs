using System;
using SousChef.Core;

internal static class ReceiptTests
{
    public static int Run()
    {
        int checks = 0;
        Action<bool, string> check = (ok, why) => { if (!ok) throw new Exception(why); checks++; };
        var parsed = ControlCommand.Parse("execute abc epoch 2 task wash_dishes 1");
        check(parsed.Fenced && parsed.Operation == "task" && parsed.Epoch == "epoch" && parsed.DispatchGeneration == 2 && parsed.Task.Action == "wash_dishes", "fenced task grammar");
        check(ControlCommand.Parse("cancel stop epoch 2").Fenced, "fenced cancellation");
        check(ControlCommand.Parse("receipt query abc").ReceiptId == "abc", "receipt lookup");
        foreach (string bad in new[] { "execute abc epoch -1 task wash_dishes 1", "execute abc epoch 1 stop", "execute abc epoch 1 execute abc epoch 1 task wash_dishes 1", "cancel abc epoch 2 extra", "receipt query bad!" })
        {
            bool rejected = false; try { ControlCommand.Parse(bad); } catch (ArgumentException) { rejected = true; }
            check(rejected, "reject invalid envelope: " + bad);
        }
        var book = new ReceiptBook(2);
        var active = book.Add("a", "payload", "epoch");
        var done = book.Add("b", "payload2", "epoch"); done.State = "succeeded";
        book.Add("c", "payload3", "epoch").State = "failed";
        check(book.Find("a") == active && book.Find("b") == null, "bound receipts while preserving active outcome");
        check(book.Find("a").Payload == "payload" && !active.Terminal, "retain canonical payload for conflict checking");
        active.State = "suspended"; check(!active.Terminal, "suspension is not completion");
        active.State = "waiting"; check(!active.Terminal, "waiting is not completion");
        active.State = "cancelled"; check(active.Terminal, "cancelled receipt is terminal");
        book.Clear(); check(book.Find("a") == null, "epoch reset clears receipts");
        return checks;
    }
}
