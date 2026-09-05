using PhoenixmlDb.Xslt;

async Task Run(string label, string body, string input = "<r/>")
{
    var xslt = $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                        xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xsl:output method="text"/>
          <xsl:template match="/r">{body}</xsl:template>
        </xsl:stylesheet>
        """;
    try
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xslt);
        Console.WriteLine($"{label,-46} OK    -> '{await t.TransformAsync(input)}'");
    }
    catch (Exception ex) { Console.WriteLine($"{label,-46} {ex.GetType().Name}"); }
}

const string Emit = "<xsl:text>x</xsl:text>";

Console.WriteLine("--- xsl:for-each over a range ---");
await Run("for-each  2 to 3",                  $"<xsl:for-each select='2 to 3'>{Emit}</xsl:for-each>");
await Run("for-each  2 to xs:integer(3)",      $"<xsl:for-each select='2 to xs:integer(3)'>{Emit}</xsl:for-each>");
await Run("for-each  2 to xs:integer(\"3\")",  $"<xsl:for-each select='2 to xs:integer(\"3\")'>{Emit}</xsl:for-each>");
await Run("for-each  2 to xs:integer(@v)",     $"<xsl:for-each select='2 to xs:integer(@v)'>{Emit}</xsl:for-each>", "<r v='3'/>");
await Run("for-each  DOCMD SHAPE (@gs,1)[1]",  $"<xsl:for-each select='2 to xs:integer((@gs, 1)[1])'>{Emit}</xsl:for-each>", "<r gs='3'/>");
await Run("for-each  DOCMD SHAPE, attr absent",$"<xsl:for-each select='2 to xs:integer((@gs, 1)[1])'>{Emit}</xsl:for-each>");

Console.WriteLine();
Console.WriteLine("--- same expressions, NOT under for-each ---");
await Run("value-of  2 to xs:integer(@v)",     "<xsl:value-of select='2 to xs:integer(@v)' separator=','/>", "<r v='3'/>");
await Run("count()   2 to xs:integer(@v)",     "<xsl:value-of select='count(2 to xs:integer(@v))'/>", "<r v='3'/>");

Console.WriteLine();
Console.WriteLine("--- other iteration constructs over the same range ---");
await Run("for $i in 2 to xs:integer(@v)",     "<xsl:value-of select='for $i in 2 to xs:integer(@v) return $i' separator=','/>", "<r v='3'/>");
await Run("xsl:iterate 2 to xs:integer(@v)",   $"<xsl:iterate select='2 to xs:integer(@v)'>{Emit}</xsl:iterate>", "<r v='3'/>");
