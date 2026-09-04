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

  <xsl:template match="w:body">
    <xsl:apply-templates select="*"/>
  </xsl:template>

  <!--
    Three sibling patterns below all match w:p with a single predicate, so without an
    explicit priority they would all default to 0.5 and the engine is entitled to raise
    an ambiguous-rule error or silently pick either. This is not hypothetical: an empty
    paragraph that carries w:outlineLvl matches both the heading and empty-paragraph
    patterns below. Priorities are assigned in most-specific-first order, with a gap left
    for list suppression (priority 2), which a later task adds:

      heading                    3
      list suppression           2  (added later)
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

  <!-- Empty paragraphs are vertical spacing in Word and mean nothing here. -->
  <xsl:template match="w:p[not(normalize-space(docmd:visible-text(.)))][not(.//w:drawing)]" priority="1"/>

  <xsl:template match="w:p" priority="0">
    <md:para>
      <xsl:apply-templates select="w:r | w:ins | w:hyperlink" mode="inline"/>
    </md:para>
  </xsl:template>

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
