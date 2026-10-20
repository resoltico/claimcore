using System;
using System.IO;
using System.Text;
using ClaimCore.Cli;
var payload = Encoding.UTF8.GetBytes("Rīga مطالبة 😀\n");
try {
    using var errors = StandardSinks.errors();
    using (var output = args[0] == "console" ? Console.OpenStandardOutput() : StandardSinks.output()) {
        output.Write(payload);
        if (args[0] == "twice") output.Write(payload);
        output.Flush();
    }
    if (args[0] == "lifetime") {
        for (var index = 0; index < 200; index++) { using var borrowed = StandardSinks.output(); borrowed.Flush(); }
        using var output = StandardSinks.output();
        output.Write(payload); output.Flush();
    }
    errors.Write(Encoding.UTF8.GetBytes(args[0] == "both" ? "STREAM_SECOND_WRITER\n" : "STREAM_WRITE_RETURNED\n"));
    errors.Flush(); return 0;
} catch (IOException) { return 70; }
catch (Exception) { return 71; }
