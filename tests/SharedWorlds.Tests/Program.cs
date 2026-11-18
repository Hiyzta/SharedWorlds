using SharedWorlds.Core;

int failures = 0;

void Check(bool condition, string name)
{
	if (condition) {
		Console.WriteLine($"PASS  {name}");
	}
	else {
		failures++;
		Console.WriteLine($"FAIL  {name}");
	}
}

static VersionRecord V(int version, string hash, string parent, ulong host = 1)
	=> new() { Version = version, Hash = hash, ParentHash = parent, HostSteamId = host, TimestampUtc = DateTime.UtcNow, WorldName = "Test" };

// Linear history: a -> b -> c
var graph = new VersionGraph();
graph.Add(V(1, "a", ""));
graph.Add(V(2, "b", "a"));
graph.Add(V(3, "c", "b"));

Check(graph.IsAncestor("a", "c"), "a is ancestor of c");
Check(!graph.IsAncestor("c", "a"), "c is not ancestor of a");
Check(graph.Compare("b", "c") == VersionRelation.RightIsNewer, "b -> c is fast-forward");
Check(graph.Compare("c", "b") == VersionRelation.LeftIsNewer, "c -> b is local-newer");
Check(graph.Compare("b", "b") == VersionRelation.Same, "same hash is same version");
Check(graph.FindHeads(new[] { V(1, "a", ""), V(2, "b", "a"), V(3, "c", "b") }).Single().Hash == "c", "linear head is c");

// Fork: a -> b -> c and b -> d
graph.Add(V(4, "d", "b"));
List<VersionRecord> heads = graph.FindHeads(new[] { V(3, "c", "b"), V(4, "d", "b") });
Check(heads.Count == 2, "fork has two heads");
Check(graph.Compare("c", "d") == VersionRelation.Unrelated, "fork branches are unrelated");
Check(graph.IsAncestor("b", "d"), "fork point is ancestor of d");

// Unknown ancestry is a head, not silently connected.
var unknown = new VersionGraph();
unknown.Add(V(5, "x", "missing"));
unknown.Add(V(6, "y", "x"));
Check(unknown.IsAncestor("x", "y"), "known chain works with missing parent");
Check(unknown.IsAncestor("missing", "y"), "parent hash is an ancestor even when its record is missing");
Check(unknown.Compare("a", "y") == VersionRelation.Unrelated, "unknown relation is not overwritten");

// Invite round-trip, including spaces and unicode.
string code = InviteCode.Create("11111111-2222-3333-4444-555555555555", 76561198000000000, 123456789, "The Vibe of Fear ✨");
Check(InviteCode.TryParse(code, out InviteData? invite, out string inviteError), "invite parses: " + inviteError);
Check(invite!.WorldId == "11111111-2222-3333-4444-555555555555", "invite world id");
Check(invite.OwnerSteamId == 76561198000000000, "invite owner");
Check(invite.ItemId == 123456789, "invite item");
Check(invite.WorldName == "The Vibe of Fear ✨", "invite name");
Check(!InviteCode.TryParse("nope", out _, out _), "bad invite rejected");

// Content hash is deterministic and covers both files.
string dir = Path.Combine(Path.GetTempPath(), "sharedworlds-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
string wld = Path.Combine(dir, "a.wld");
string twld = Path.Combine(dir, "a.twld");
File.WriteAllText(wld, "world");
File.WriteAllText(twld, "mod data");
string h1 = HashUtil.ComputeContentHash(wld, twld);
string h2 = HashUtil.ComputeContentHash(wld, twld);
Check(h1 == h2 && h1.Length == 64, "content hash deterministic");
File.WriteAllText(twld, "mod data changed");
Check(HashUtil.ComputeContentHash(wld, twld) != h1, "content hash changes with twld");
Directory.Delete(dir, true);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "All tests passed." : $"{failures} test(s) failed.");
return failures == 0 ? 0 : 1;
