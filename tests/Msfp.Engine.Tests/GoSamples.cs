namespace Msfp.Tests;

// What the Go engine wrote (msfp-go v0.2.0), made once with a throwaway
// program: a KeySealer box under a known key, and a directory of the Go
// JSON file store holding one of every record. The C# engine must open
// both as they are.
internal static class GoSamples
{
    // KeySealer(key 00 01 … 1f).Seal("hello msfp", "msfp current key").
    internal const string KeyBox = "72e03b00a6c9ae55bed1b14c613aeda3ce4fe63a58e13bb4d6e64269532eb45c562a984b312a";

    // The directory's Sealer().Seal("secret", "msfp ids secret"), under SealingKey.
    internal const string DirBox = "4494efd5777260538bb6ecc1014caaf0bd43958bebd3c850650832bdd85c2b472304";

    internal const string State = """
        {"server":{"current":"Y3Vy","nextCommitment":"bmM=","idsSecret":"aWRz","address":"https://a.example","former":"https://old.example","movedAt":"2026-11-18T21:30:15.123456789Z","reached":true},"peers":{"p1":{"id":"p1","key":"cGVlciBrZXk=","name":"One","created":"2026-11-18T21:30:15.123456789Z","state":"paired","wasPaired":true,"paired":"2026-11-19T00:00:00Z","address":"https://p1.example","move":{"address":"https://p1b.example","since":"2026-11-18T21:30:15.123456789Z"},"inbound":"yes","versions":{"core":[1],"parts":{"catalog":[1]}},"pins":["pin1"],"pending":{"Old":"bw==","New":"bg==","Next":null,"Date":42,"OldSignature":"czE=","NewSignature":"czI="},"ourKey":"old","wants":[{"id":"L1","name":"Films","kind":"movies","wanted":true}],"limits":{"streams":2,"transcodes":1},"theirLimits":{"streams":3,"transcodes":0},"catalogRead":{"seq":7,"wants":["L1"]}},"p2":{"id":"p2","key":"b3RoZXI=","name":"Two","created":"0001-01-01T00:00:00Z","state":"","address":"","inbound":"","limits":{"streams":0,"transcodes":0}}},"codes":{"c1":{"id":"c1","secret":"c2Vj","secretHash":"aGFzaA==","created":"2026-11-18T21:30:15.123456789Z","expires":"2026-11-19T00:00:00Z"}},"forgotten":{"6b68":{"keyHash":"a2g=","at":"2026-11-18T21:30:15.123456789Z","revoked":true}},"libraryIds":{"host1":"pub1"},"leftovers":{"p1\npb1":{"peer":"p1","playbackId":"pb1"}}}
        """;

    internal const string Own = """
        {"snapshot":{"movie/tmdb/5":{"video":{"key":"movie/tmdb/5","kind":"movie","libraries":null}}},"changes":[{"seq":1,"key":"movie/tmdb/5","title":{"video":{"key":"movie/tmdb/5","kind":"movie","libraries":null}},"at":"2026-11-18T21:30:15.123456789Z"},{"seq":2,"key":"movie/tmdb/6","at":"2026-11-19T00:00:00Z"}],"floor":0,"seq":2}
        """;

    internal const string PeerP1 = """
        {"titles":{"movie/tmdb/1":{"reading":3,"title":{"video":{"key":"movie/tmdb/1","kind":"movie","libraries":["L1"],"audio":["fr"],"video":{"height":2160,"hdr":["dv"]}}}},"track/mb/x":{"reading":3,"title":{"music":{"key":"track/mb/x","kind":"track","libraries":null,"name":"Song","disc":1,"number":2,"durationMs":1000}}}},"sent":{"movie/tmdb/9":true}}
        """;

    internal const string SealingKey = "cd6788d5004a98d88515fe13705a6cf8d7b5a7c783c7b4dfc359ebc11a883de2";

    // Mode is a file's Unix mode; the tests that check modes run where
    // there are some.
    internal static UnixFileMode Mode(string path) =>
        OperatingSystem.IsWindows() ? throw new PlatformNotSupportedException() : File.GetUnixFileMode(path);
}
