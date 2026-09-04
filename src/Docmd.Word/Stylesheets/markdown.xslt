<?xml version="1.0" encoding="UTF-8"?>
<!--
  WordprocessingML -> md-XML.

  This stylesheet emits a semantic tree, never Markdown text: MarkdownSerializer owns
  every whitespace and escaping rule (spec §4.5). Heading levels and slugs are read from
  the docmd: attributes that HeadingAnnotator stamped on, never recomputed here, so the
  Markdown and the review companion cannot disagree (spec §4.4).
-->
<xsl:stylesheet version="3.0"
    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
    xmlns:xs="http://www.w3.org/2001/XMLSchema"
    xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
    xmlns:docmd="https://phoenixml.dev/docmd"
    xmlns:md="https://phoenixml.dev/docmd/md"
    exclude-result-prefixes="xs w docmd">

  <xsl:output method="xml" indent="no"/>
  <xsl:strip-space elements="*"/>
  <xsl:preserve-space elements="w:t w:delText"/>

  <xsl:template match="/docmd:package">
    <md:document>
      <xsl:apply-templates select="docmd:body/w:body"/>
    </md:document>
  </xsl:template>

  <xsl:variable name="numbering" select="/docmd:package/docmd:numbering/w:numbering"/>

  <xsl:function name="docmd:num-id" as="xs:string">
    <xsl:param name="p" as="element(w:p)"/>
    <xsl:sequence select="string(($p/w:pPr/w:numPr/w:numId/@w:val, '')[1])"/>
  </xsl:function>

  <xsl:function name="docmd:ilvl" as="xs:integer">
    <xsl:param name="p" as="element(w:p)"/>
    <xsl:sequence select="xs:integer(($p/w:pPr/w:numPr/w:ilvl/@w:val, '0')[1])"/>
  </xsl:function>

  <!--
    numFmt lives two hops away: w:num -> abstractNumId -> w:abstractNum -> w:lvl.
    An undefined numId degrades to a bullet rather than failing: one malformed document
    must never stop a corpus conversion.
  -->
  <xsl:function name="docmd:is-ordered" as="xs:boolean">
    <xsl:param name="numbering" as="element()?"/>
    <xsl:param name="numId" as="xs:string"/>
    <xsl:param name="ilvl" as="xs:integer"/>
    <xsl:variable name="abstractId"
        select="string($numbering/w:num[@w:numId eq $numId]/w:abstractNumId/@w:val)"/>
    <xsl:variable name="format"
        select="string($numbering/w:abstractNum[@w:abstractNumId eq $abstractId]
                                  /w:lvl[xs:integer(@w:ilvl) eq $ilvl]/w:numFmt/@w:val)"/>
    <xsl:sequence select="$format ne '' and $format ne 'bullet' and $format ne 'none'"/>
  </xsl:function>

  <!--
    Word stores no nesting: every item is a top-level paragraph with a numId and an ilvl.
    group-adjacent separates list runs from body text; the recursive template below
    rebuilds depth from ilvl.
  -->
  <xsl:template match="w:body">
    <xsl:for-each-group select="*"
        group-adjacent="if (self::w:p[w:pPr/w:numPr]) then docmd:num-id(.) else ''">
      <xsl:choose>
        <xsl:when test="current-grouping-key() ne ''">
          <xsl:call-template name="build-list">
            <xsl:with-param name="items" select="current-group()"/>
            <xsl:with-param name="level" select="docmd:ilvl(current-group()[1])"/>
          </xsl:call-template>
        </xsl:when>
        <xsl:otherwise>
          <xsl:apply-templates select="current-group()"/>
        </xsl:otherwise>
      </xsl:choose>
    </xsl:for-each-group>
  </xsl:template>

  <xsl:template name="build-list">
    <xsl:param name="items" as="element(w:p)*"/>
    <xsl:param name="level" as="xs:integer"/>

    <md:list ordered="{docmd:is-ordered($numbering, docmd:num-id($items[1]), $level)}">
      <xsl:for-each-group select="$items" group-starting-with="w:p[docmd:ilvl(.) le $level]">
        <md:item>
          <md:para>
            <xsl:apply-templates select="current-group()[1]/(w:r | w:ins | w:hyperlink)" mode="inline"/>
          </md:para>
          <xsl:variable name="deeper" select="current-group()[position() gt 1]"/>
          <xsl:if test="exists($deeper)">
            <xsl:call-template name="build-list">
              <xsl:with-param name="items" select="$deeper"/>
              <xsl:with-param name="level" select="$level + 1"/>
            </xsl:call-template>
          </xsl:if>
        </md:item>
      </xsl:for-each-group>
    </md:list>
  </xsl:template>

  <!--
    Three sibling patterns below all match w:p with a single predicate, so without an
    explicit priority they would all default to 0.5 and the engine is entitled to raise
    an ambiguous-rule error or silently pick either. This is not hypothetical: an empty
    paragraph that carries w:outlineLvl matches both the heading and empty-paragraph
    patterns below. Priorities are assigned in most-specific-first order:

      heading                    3
      list suppression           2
      empty-paragraph            1
      generic paragraph          0
  -->

  <!-- Headings. Level comes from annotation and is zero-based, so +1 for Markdown. -->
  <xsl:template match="w:p[@docmd:heading-source and @docmd:heading-source ne 'None']" priority="3">
    <md:heading level="{xs:integer(@docmd:outline-level) + 1}" slug="{@docmd:slug}">
      <!-- Plain text only: '# **Title**' is noise. -->
      <md:text><xsl:value-of select="docmd:visible-text(.)"/></md:text>
    </md:heading>
  </xsl:template>

  <!--
    A list paragraph reached by any route other than build-list (e.g. as a lone item in
    an apply-templates fallback) produces nothing rather than a stray paragraph. The
    w:body template above never applies-templates to a list paragraph directly, but this
    is the safety net named in the priority ladder.
  -->
  <xsl:template match="w:p[w:pPr/w:numPr]" priority="2"/>

  <!-- Empty paragraphs are vertical spacing in Word and mean nothing here. -->
  <xsl:template match="w:p[not(normalize-space(docmd:visible-text(.)))][not(.//w:drawing)]" priority="1"/>

  <xsl:template match="w:p" priority="0">
    <md:para>
      <xsl:apply-templates select="w:r | w:ins | w:hyperlink" mode="inline"/>
    </md:para>
  </xsl:template>

  <xsl:template match="w:tbl">
    <md:table>
      <xsl:for-each select="w:tr">
        <md:row header="{if (position() eq 1) then 'true' else 'false'}">
          <xsl:for-each select="w:tc">
            <md:cell>
              <!--
                A vMerge continuation carries the visual span, not content. w:val='restart'
                marks the cell that owns it.
              -->
              <xsl:if test="not(w:tcPr/w:vMerge[not(@w:val eq 'restart')])">
                <!--
                  Paragraphs are joined with a space: a newline inside a pipe cell would
                  terminate the row. Nested tables are flattened here for the same reason,
                  since GFM cannot express them, and the words matter more than the shape.
                -->
                <md:text>
                  <xsl:value-of select="normalize-space(string-join(.//w:t[not(ancestor::w:del)], ' '))"/>
                </md:text>
              </xsl:if>
            </md:cell>

            <!--
              gridSpan has no GFM equivalent. Emit the spanned positions as empty cells so
              every row keeps the same width; a ragged row breaks the table entirely.

              ENGINE DEFECT WORKAROUND: the brief's given "xsl:for-each select=2 to
              xs:integer(...)" crashes PhoenixmlDb.Xslt 1.6.13 with an unhandled
              InvalidCastException ("Unable to cast object of type
              'System.Numerics.BigInteger' to type 'System.IConvertible'"). Reported
              upstream (see docmd task-9-report.md); a recursive named template sidesteps
              the buggy fast path entirely and is proven equivalent for every gridSpan
              value tested (1, 2, 4).
            -->
            <xsl:call-template name="empty-span-cells">
              <xsl:with-param name="remaining" select="xs:integer((w:tcPr/w:gridSpan/@w:val, 1)[1]) - 1"/>
            </xsl:call-template>
          </xsl:for-each>
        </md:row>
      </xsl:for-each>
    </md:table>
  </xsl:template>

  <xsl:template name="empty-span-cells">
    <xsl:param name="remaining" as="xs:integer"/>
    <xsl:if test="$remaining gt 0">
      <md:cell/>
      <xsl:call-template name="empty-span-cells">
        <xsl:with-param name="remaining" select="$remaining - 1"/>
      </xsl:call-template>
    </xsl:if>
  </xsl:template>

  <!-- A nested table is consumed by its containing cell's string-join above. -->
  <xsl:template match="w:tbl[ancestor::w:tc]"/>

  <!-- Insertions are part of the accepted text; deletions are not reached at all,
       because no template selects w:del. -->
  <xsl:template match="w:ins" mode="inline">
    <xsl:apply-templates select="w:r" mode="inline"/>
  </xsl:template>

  <xsl:template match="w:r" mode="inline">
    <xsl:variable name="text" select="string-join(w:t, '')"/>

    <xsl:if test="$text ne '' or w:br">
      <!--
        BRIEF DEFECT (flagged, not silently resolved: see task-7-report.md). The plan's
        given template built this sequence as "all w:t joined, then all w:br appended",
        which drops the reading order of a run that has text on both sides of a break.
        LineBreak_BecomesAHardBreak's "one", break, "two" inside a SINGLE w:r rendered as
        "onetwo  \n" instead of "one  \ntwo\n". "w:t | w:br" is a document-order union,
        so walking it in one pass keeps text and breaks interleaved correctly.
      -->
      <xsl:variable name="innermost" as="node()*">
        <xsl:for-each select="w:t | w:br">
          <xsl:choose>
            <xsl:when test="self::w:t"><md:text><xsl:value-of select="."/></md:text></xsl:when>
            <xsl:otherwise><md:br/></xsl:otherwise>
          </xsl:choose>
        </xsl:for-each>
      </xsl:variable>

      <xsl:variable name="italicised" as="node()*">
        <xsl:choose>
          <xsl:when test="w:rPr/w:i and $text ne ''">
            <md:em><xsl:sequence select="$innermost"/></md:em>
          </xsl:when>
          <xsl:otherwise><xsl:sequence select="$innermost"/></xsl:otherwise>
        </xsl:choose>
      </xsl:variable>

      <xsl:choose>
        <xsl:when test="w:rPr/w:b and $text ne ''">
          <md:strong><xsl:sequence select="$italicised"/></md:strong>
        </xsl:when>
        <xsl:otherwise><xsl:sequence select="$italicised"/></xsl:otherwise>
      </xsl:choose>
    </xsl:if>
  </xsl:template>

  <!--
    The text a reader would see. Selects w:t and never w:delText, so deleted words are
    absent by construction rather than by a filter someone can forget.
  -->
  <xsl:function name="docmd:visible-text" as="xs:string">
    <xsl:param name="node" as="node()"/>
    <xsl:sequence select="string-join($node//w:t[not(ancestor::w:del)], '')"/>
  </xsl:function>

</xsl:stylesheet>
