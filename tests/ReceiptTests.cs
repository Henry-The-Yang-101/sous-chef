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
        var admitted = ControlCommand.Parse("enqueue queued epoch 2 1 task prepare_near_player 1 1 sZmlzaA chopped 2");
        check(admitted.Scheduled && admitted.Priority && admitted.Fenced && admitted.Task.Items[0].Quantity == 2, "fenced native admission retains quantities and urgency");
        check(ControlCommand.Parse("schedule_hold hold epoch 2 1").Priority, "dispatch hold uses native fencing");
        foreach (var invalid in new[] { "enqueue x epoch 0 7 task wash_dishes 1", "enqueue x epoch 0 0 pick_up d1", "schedule_hold x epoch 0 yes" })
        {
            bool rejected = false; try { ControlCommand.Parse(invalid); } catch (ArgumentException) { rejected = true; }
            check(rejected, "invalid admission rejected");
        }
        var blocked = new WorkOffer { Ready = false, Class = 0, Slack = -5 };
        var ordinary = new WorkOffer { Class = 3, Remaining = 10 };
        check(ordinary.PreferTo(blocked) && !blocked.PreferTo(ordinary), "blocked urgent work never occupies the ready queue");
        var rescue = new WorkOffer { Class = 0, Slack = 2, Remaining = 3 };
        check(rescue.PreferTo(ordinary), "imminent rescue outranks ordinary completion");
        check(new WorkOffer { Class = 3, Remaining = 2 }.PreferTo(ordinary), "finish a useful portion before starting equally urgent work");
        check(!new WorkOffer { Class = 3, Remaining = 9.5f, Cost = 2 }.PreferTo(ordinary), "small fluctuations do not justify a context switch");
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
