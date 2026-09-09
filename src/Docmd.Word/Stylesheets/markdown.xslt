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
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    xmlns:md="https://phoenixml.dev/docmd/md"
    xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
    xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
    xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"
    xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
    exclude-result-prefixes="xs w mc docmd wp a pic r">

  <xsl:output method="xml" indent="no"/>
  <xsl:strip-space elements="*"/>
  <xsl:preserve-space elements="w:t w:delText"/>

  <xsl:template match="/docmd:package">
    <md:document>
      <xsl:apply-templates select="docmd:body/w:body"/>
    </md:document>
  </xsl:template>

  <!--
    A style map entry for a paragraph or run style, matched on w:styleId first and then on the
    style's localised w:name, so a map written against either works. The map rides in the
    composite rather than arriving as an xsl:param: the engine cannot hold a node in a parameter
    (docs/engine-defects/2026-09-04-xslt-node-valued-parameters.md), and riding here keeps the
    transform a pure function of one input anyway.
  -->
  <!--
    Resolving the localised w:name means scanning every w:style definition (142 of them in the
    document this was profiled against), and that runs for every styled paragraph AND every
    styled run. Ordering the tests so the map is checked first matters more than it looks:
    with no map, or with one whose keys are all styleIds, the scan is dead work. It was
    costing 31% of the transform on a 1,000 paragraph document. The name lookup is the
    fallback it always was; it just no longer runs when nothing can consume it.
  -->
  <xsl:function name="docmd:style-rule" as="element()?">
    <xsl:param name="node" as="node()"/>
    <xsl:param name="styleId" as="xs:string"/>
    <xsl:variable name="map" select="root($node)/docmd:package/docmd:style-map"/>
    <xsl:choose>
      <xsl:when test="$styleId eq '' or empty($map/docmd:style)">
        <xsl:sequence select="()"/>
      </xsl:when>
      <xsl:otherwise>
        <xsl:variable name="byId" select="($map/docmd:style[@key eq $styleId])[1]"/>
        <xsl:choose>
          <xsl:when test="exists($byId)">
            <xsl:sequence select="$byId"/>
          </xsl:when>
          <xsl:otherwise>
            <xsl:variable name="name"
                select="string((root($node)/docmd:package/docmd:styles/w:styles
                                /w:style[@w:styleId eq $styleId])[1]/w:name/@w:val)"/>
            <xsl:sequence select="($map/docmd:style[$name ne '' and @key eq $name])[1]"/>
          </xsl:otherwise>
        </xsl:choose>
      </xsl:otherwise>
    </xsl:choose>
  </xsl:function>

  <xsl:function name="docmd:para-rule" as="element()?">
    <xsl:param name="p" as="element(w:p)"/>
    <xsl:sequence select="docmd:style-rule($p, string($p/w:pPr/w:pStyle/@w:val))"/>
  </xsl:function>

  <xsl:variable name="numbering" select="/docmd:package/docmd:numbering/w:numbering"/>

  <!--
    Relationship targets were resolved during assembly, so this is a lookup, never path
    arithmetic.
  -->
  <xsl:key name="rel" match="docmd:relationship" use="@id"/>

  <xsl:function name="docmd:rel-target" as="xs:string">
    <xsl:param name="package" as="document-node()"/>
    <xsl:param name="id" as="xs:string?"/>
    <xsl:sequence select="string(key('rel', $id, $package)/@target)"/>
  </xsl:function>

  <xsl:template match="w:hyperlink" mode="inline">
    <xsl:variable name="href" select="docmd:rel-target(root(.), @r:id)"/>
    <xsl:choose>
      <xsl:when test="$href ne ''">
        <md:link href="{$href}">
          <xsl:apply-templates select="w:r" mode="inline"/>
        </md:link>
      </xsl:when>
      <!-- A dangling r:id degrades to plain text: the words matter, the link does not. -->
      <xsl:otherwise>
        <xsl:apply-templates select="w:r" mode="inline"/>
      </xsl:otherwise>
    </xsl:choose>
  </xsl:template>

  <!--
    src carries the PART NAME. AssetRewriter replaces it with the sink's URI before
    serialisation, so the stylesheet stays ignorant of where assets go.
    Alt text is the only thing an image contributes to a text index, so both descr and
    title are tried.
  -->
  <xsl:template match="w:drawing" mode="inline">
    <xsl:variable name="embed" select="(.//a:blip/@r:embed)[1]"/>
    <xsl:variable name="target" select="docmd:rel-target(root(.), $embed)"/>
    <xsl:if test="$target ne ''">
      <md:image src="{$target}"
                alt="{((.//wp:docPr/@descr)[1], (.//wp:docPr/@title)[1], '')[1]}"/>
    </xsl:if>
  </xsl:template>

  <!--
    The numbering id actually in effect, or '' when there is none. w:numId 0 is not a
    list: it is how Word CANCELS numbering a paragraph would otherwise inherit from its
    style, so treating it as an id turns every opted-out paragraph into a bullet and
    groups consecutive ones into a list that does not exist in the document.
  -->
  <xsl:function name="docmd:num-id" as="xs:string">
    <xsl:param name="p" as="element(w:p)"/>
    <xsl:variable name="id" select="string(($p/w:pPr/w:numPr/w:numId/@w:val, '')[1])"/>
    <xsl:variable name="rule" select="docmd:para-rule($p)"/>
    <!--
      A style mapped to a list kind joins the same grouping key numbering uses, so consecutive
      mapped items form ONE list instead of a run of single-item lists with blank lines between.
    -->
    <xsl:sequence select="if ($rule/@as = ('list-item','ordered-list-item'))
                          then concat('map:', $rule/@as)
                          else if ($id eq '0') then '' else $id"/>
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
    <!--
      Both lookups take the first match rather than the only one. Nothing enforces that a
      numbering part declares each w:numId or each w:lvl once, and real documents (merged
      files especially) declare them twice. fn:string() on a two-item sequence is a
      dynamic error, which propagated out of ConvertAsync and ended the whole batch run,
      contradicting the promise three lines above that one malformed document must never
      stop a corpus conversion.
    -->
    <xsl:choose>
      <xsl:when test="starts-with($numId, 'map:')">
        <!-- A mapped list declares its own kind; numbering.xml has nothing to say about it. -->
        <xsl:sequence select="$numId eq 'map:ordered-list-item'"/>
      </xsl:when>
      <xsl:otherwise>
    <xsl:variable name="abstractId"
        select="string(($numbering/w:num[@w:numId eq $numId]/w:abstractNumId/@w:val)[1])"/>
    <xsl:variable name="format"
        select="string(($numbering/w:abstractNum[@w:abstractNumId eq $abstractId]
                                  /w:lvl[xs:integer(@w:ilvl) eq $ilvl]/w:numFmt/@w:val)[1])"/>
    <xsl:sequence select="$format ne '' and $format ne 'bullet' and $format ne 'none'"/>
      </xsl:otherwise>
    </xsl:choose>
  </xsl:function>

  <!--
    Word stores no nesting: every item is a top-level paragraph with a numId and an ilvl.
    group-adjacent separates list runs from body text; the recursive template below
    rebuilds depth from ilvl.
  -->
  <xsl:template match="w:body">
    <xsl:for-each-group select="*"
        group-adjacent="if (self::w:p) then docmd:num-id(.) else ''">
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
            <xsl:apply-templates select="current-group()[1]/(w:r | w:ins | w:hyperlink | w:sdt | w:fldSimple | w:smartTag)" mode="inline"/>
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
    The sibling patterns below all match w:p with a single predicate, so without an
    explicit priority they would all default to 0.5 and the engine is entitled to raise
    an ambiguous-rule error or silently pick either. This is not hypothetical: an empty
    paragraph that carries w:outlineLvl matches both the heading and empty-paragraph
    patterns below. Priorities are assigned in most-specific-first order:

      heading                    3
      empty-paragraph            1
      generic paragraph          0

    There is deliberately no rule suppressing w:p[w:pPr/w:numPr]. One existed, described
    as an unreachable safety net for a list paragraph arriving by some route other than
    build-list, and it emitted nothing. It was reachable by at least two routes, and on
    both of them it deleted the paragraph's words: a w:numPr carrying w:ilvl but no
    w:numId (numbering inherited from the style, so w:body's grouping key is '' and the
    paragraph is applied directly), and a block-level w:sdt wrapping a numbered paragraph
    (w:body groups the w:sdt, and the built-in rule walks into it). Producing nothing is
    the one outcome spec §14 forbids; falling through to the generic paragraph rule below
    loses the list marker and keeps the text, which is the right trade.
  -->

  <!--
    A mapped paragraph style, at priority 4 so an explicit instruction beats every inference
    below it. List kinds are absent on purpose: those join the numbering grouping in
    docmd:num-id, so build-list emits them and consecutive items form one list.

    A prefix is LITERAL TEXT and is escaped like any other document text. Markdown in a prefix
    would have to be injected raw, which is a good way to let one style map corrupt every
    document it touches; anyone wanting emphasis there has the stylesheet override.
  -->
  <xsl:template match="w:p[docmd:para-rule(.)/@as = ('heading','blockquote','code-block','para')]"
                priority="4">
    <xsl:variable name="rule" select="docmd:para-rule(.)"/>
    <xsl:variable name="prefix" select="string($rule/@prefix)"/>
    <xsl:choose>
      <xsl:when test="$rule/@as eq 'heading'">
        <md:heading level="{if ($rule/@level ne '') then $rule/@level else '1'}"
                    slug="{@docmd:slug}">
          <md:text><xsl:value-of select="concat($prefix, docmd:visible-text(.))"/></md:text>
        </md:heading>
      </xsl:when>
      <xsl:when test="$rule/@as eq 'code-block'">
        <md:code-block><xsl:value-of select="concat($prefix, docmd:visible-text(.))"/></md:code-block>
      </xsl:when>
      <xsl:when test="$rule/@as eq 'blockquote'">
        <md:blockquote>
          <md:para>
            <xsl:if test="$prefix ne ''"><md:text><xsl:value-of select="$prefix"/></md:text></xsl:if>
            <xsl:apply-templates select="w:r | w:ins | w:hyperlink | w:sdt | w:fldSimple | w:smartTag" mode="inline"/>
          </md:para>
        </md:blockquote>
      </xsl:when>
      <xsl:otherwise>
        <md:para>
          <xsl:if test="$prefix ne ''"><md:text><xsl:value-of select="$prefix"/></md:text></xsl:if>
          <xsl:apply-templates select="w:r | w:ins | w:hyperlink | w:sdt | w:fldSimple | w:smartTag" mode="inline"/>
        </md:para>
      </xsl:otherwise>
    </xsl:choose>
  </xsl:template>

  <!-- Headings. Level comes from annotation and is zero-based, so +1 for Markdown. -->
  <xsl:template match="w:p[@docmd:heading-source and @docmd:heading-source ne 'None']" priority="3">
    <md:heading level="{xs:integer(@docmd:outline-level) + 1}" slug="{@docmd:slug}">
      <!-- Plain text only: '# **Title**' is noise. -->
      <md:text><xsl:value-of select="docmd:visible-text(.)"/></md:text>
    </md:heading>
  </xsl:template>

  <!-- Empty paragraphs are vertical spacing in Word and mean nothing here. -->
  <!--
    ONE predicate, not two, and that is a performance contract rather than a style choice.
    Two chained predicates on a match pattern cost this engine a document wide scan per
    candidate node, making the whole transform quadratic: on a synthetic 500 paragraph
    document the two predicate form runs at 54.1 ms per paragraph and this one at 0.32,
    a factor of 169. Joining them with "and" is semantically identical here because neither
    predicate is positional. Engine defect: docs/engine-defects/2026-09-05-xslt-chained-predicates-in-match-patterns.md
  -->
  <xsl:template match="w:p[not(normalize-space(docmd:own-text(.)))
                          and not(.//w:drawing) and not(.//w:txbxContent)]"
                priority="1"/>

  <!--
    A text box holds paragraphs, not runs, so its content cannot be emitted inline where the
    box is anchored. It becomes sibling blocks immediately after the anchoring paragraph, which
    is the closest honest reading of a floating box in a linear document: its words survive, in
    roughly the place a reader met them.

    Only outermost boxes are selected. A box nested inside another would otherwise be emitted
    twice, once by this paragraph and once by the box paragraph that also contains it, and
    duplicating content is a worse failure than the vanishingly rare nested box it protects.
  -->
  <xsl:template match="w:p" priority="0">
    <xsl:if test="normalize-space(docmd:own-text(.)) ne '' or .//w:drawing">
      <md:para>
        <xsl:apply-templates select="w:r | w:ins | w:hyperlink | w:sdt | w:fldSimple | w:smartTag" mode="inline"/>
      </md:para>
    </xsl:if>
    <xsl:apply-templates
        select=".//w:txbxContent[not(ancestor::w:txbxContent)][not(ancestor::mc:Fallback)]/w:p"/>
  </xsl:template>

  <!--
    Rows and cells are found by descent rather than as children, because they are frequently
    not children. Word wraps them in w:customXml (the Word 2003 custom markup that predates
    content controls) and in block level w:sdt, either of which sits between a table and its
    rows and between a row and its cells. Selecting w:tr as a child silently produced an empty
    table: one invoice lost every line item that way, 72 of its 184 words.

    Nearest-ancestor identity rather than a bare .//w:tr, so a nested table's rows belong to the
    nested table and are not also claimed by the outer one. Nested tables are flattened into the
    containing cell further down, and claiming their rows twice would print them twice.
  -->
  <xsl:template match="w:tbl">
    <xsl:variable name="table" select="."/>
    <md:table>
      <xsl:for-each select=".//w:tr[ancestor::w:tbl[1] is $table]">
        <xsl:variable name="row" select="."/>
        <md:row header="{if (position() eq 1) then 'true' else 'false'}">
          <xsl:for-each select=".//w:tc[ancestor::w:tr[1] is $row]">
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

                  Within one paragraph the runs are joined with NOTHING. Joining every w:t
                  with a space inserts one wherever Word split a run, which it does
                  constantly, so a cell reading "conversion" came out as "conver sion".
                  This is the same defect MergeAdjacentMarkup fixed for inline text; a
                  table cell reaches the serialiser as a single md:text, so it has to be
                  fixed here as well as there.

                  w:br and w:tab are therefore selected explicitly and contribute a space
                  each. They used to get one by accident, from the w:t separator this cell
                  no longer has, and losing it welded "A", break, "B" into "AB" and
                  "Name", tab, "Value" into "NameValue": the exact word-welding this join
                  was changed to stop, reappearing in the one path the inline emitters
                  never reach. A break cannot survive as a break inside a pipe
                  row, so a space is the closest honest reading; normalize-space below
                  collapses whatever this produces.
                -->
                <md:text>
                  <xsl:value-of select="normalize-space(
                      string-join(
                          for $p in .//w:p[not(ancestor::w:txbxContent)]
                          return string-join(
                              for $n in $p//*[self::w:t or self::w:br or self::w:tab]
                                             [not(ancestor::w:del)]
                              return if ($n/self::w:t) then string($n) else ' ',
                              ''),
                          ' '))"/>
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
  <!--
    Wrappers a reader never sees, holding text a reader does see.

    A content control carries the variable parts of a templated document, which is to say the
    customer name, the revision and the approval block: precisely the fields most worth
    indexing. A field carries a cached result, and measured across the corpus this was built
    against those results are 103 DOCPROPERTY, 12 SEQ, a TITLE and an AUTHOR, with no PAGE and
    no TOC. Every one of them is content. A smart tag is a recognition marker around ordinary
    words.

    The elements deliberately still left out are the ones whose text a reader is NOT shown:
    w:instrText holds a field code rather than its result, and w:del holds text the author
    removed under track changes. Emitting those would put "PAGE \* MERGEFORMAT" and a deleted
    price into a document someone indexes, which is a worse failure than omission because it
    invents content instead of missing it.

    This remains a whitelist and will therefore always be incomplete, because OOXML keeps
    growing. The text-coverage check is what makes that survivable: a document whose words go
    missing now says so rather than converting quietly.
  -->
  <xsl:template match="w:sdt" mode="inline">
    <xsl:apply-templates select="w:sdtContent/(w:r | w:ins | w:hyperlink | w:sdt | w:fldSimple | w:smartTag)" mode="inline"/>
  </xsl:template>

  <xsl:template match="w:fldSimple | w:smartTag" mode="inline">
    <xsl:apply-templates select="w:r | w:ins | w:hyperlink | w:sdt | w:fldSimple | w:smartTag" mode="inline"/>
  </xsl:template>

  <xsl:template match="w:ins" mode="inline">
    <xsl:apply-templates select="w:r" mode="inline"/>
  </xsl:template>

  <xsl:template match="w:r" mode="inline">
    <xsl:variable name="text" select="string-join(w:t, '')"/>

    <xsl:if test="$text ne '' or w:br or w:tab or w:drawing">
      <!--
        BRIEF DEFECT (flagged, not silently resolved: see task-7-report.md). The plan's
        given template built this sequence as "all w:t joined, then all w:br appended",
        which drops the reading order of a run that has text on both sides of a break.
        LineBreak_BecomesAHardBreak's "one", break, "two" inside a SINGLE w:r rendered as
        "onetwo  \n" instead of "one  \ntwo\n". "w:t | w:br" is a document-order union,
        so walking it in one pass keeps text and breaks interleaved correctly.

        Task 10 REPEATS this fix for w:drawing rather than the plan's given
        "apply-templates select='w:drawing' after the loop", which is the identical bug
        in a new costume: an inline image between two text runs would jump to the end.
        Folding w:drawing into the same document-order union keeps it in true position.
      -->
      <xsl:variable name="innermost" as="node()*">
        <xsl:for-each select="w:t | w:br | w:tab | w:drawing">
          <xsl:choose>
            <xsl:when test="self::w:t"><md:text><xsl:value-of select="."/></md:text></xsl:when>
            <xsl:when test="self::w:br"><md:br/></xsl:when>
            <!--
              A tab is a word separator, not decoration: Markdown has no tab stops, and
              omitting it welded "Name" and "Value" into "NameValue". One space is the
              closest honest reading. xsl:text, because a whitespace-only text node
              written directly in a stylesheet is stripped from it before it ever runs.
            -->
            <xsl:when test="self::w:tab"><md:text><xsl:text> </xsl:text></md:text></xsl:when>
            <xsl:otherwise><xsl:apply-templates select="." mode="inline"/></xsl:otherwise>
          </xsl:choose>
        </xsl:for-each>
      </xsl:variable>

      <!--
        w:b and w:i are ST_OnOff toggles, not flags. An existence test reads
        <w:b w:val="0"/> as bold, and that is not a corner case: it is how Word expresses
        "turn bold OFF against a style that turns it on", which is what every run of body
        text inside a bold-styled block looks like.
      -->
      <xsl:variable name="charRule"
          select="docmd:style-rule(., string(w:rPr/w:rStyle/@w:val))[@as = ('inline-code','strong','em')]"/>

      <xsl:choose>
        <!--
          A mapped character style REPLACES the toggle inference for that run rather than
          layering over it. Explicit instruction beats inference, exactly as it does for a
          mapped paragraph. The alternative means answering what a run that is both bold and
          mapped to code should be, and there is no honest answer: a code span renders no markup
          inside it, so either layering order misrepresents one of the two.
        -->
        <xsl:when test="exists($charRule) and $text ne ''">
          <xsl:choose>
            <xsl:when test="$charRule/@as eq 'inline-code'"><md:code><xsl:sequence select="$innermost"/></md:code></xsl:when>
            <xsl:when test="$charRule/@as eq 'strong'"><md:strong><xsl:sequence select="$innermost"/></md:strong></xsl:when>
            <xsl:otherwise><md:em><xsl:sequence select="$innermost"/></md:em></xsl:otherwise>
          </xsl:choose>
        </xsl:when>
        <xsl:otherwise>
          <xsl:variable name="italicised" as="node()*">
            <xsl:choose>
              <xsl:when test="w:rPr/w:i[not(@w:val = ('0','false','off'))] and $text ne ''">
                <md:em><xsl:sequence select="$innermost"/></md:em>
              </xsl:when>
              <xsl:otherwise><xsl:sequence select="$innermost"/></xsl:otherwise>
            </xsl:choose>
          </xsl:variable>

          <xsl:choose>
            <xsl:when test="w:rPr/w:b[not(@w:val = ('0','false','off'))] and $text ne ''">
              <md:strong><xsl:sequence select="$italicised"/></md:strong>
            </xsl:when>
            <xsl:otherwise><xsl:sequence select="$italicised"/></xsl:otherwise>
          </xsl:choose>
        </xsl:otherwise>
      </xsl:choose>
    </xsl:if>
  </xsl:template>

  <!--
    The text a reader would see. Selects w:t and never w:delText, so deleted words are
    absent by construction rather than by a filter someone can forget.
  -->
  <!--
    w:tab and w:br are word separators, not decoration, and they contribute one space each.
    Selecting only w:t welded the words on either side of a tab: a heading reading
    "Name<tab>Value" came out as "NameValue", and a real signature block reading
    "SIGNATURE<tab><tab><tab>SIGNATURE" came out as one run of underscores. This is the same
    defect already fixed twice, once for inline runs and once for table cells; this function
    serves headings and code blocks and was missed both times. Found by the text preservation
    oracle on its first corpus run.

    Paragraphs that hold nothing but tabs are unaffected: the empty paragraph rule tests
    normalize-space of this value, and a string of spaces still normalizes to nothing.
  -->
  <!--
    A paragraph's own text, excluding any text box anchored INSIDE it.

    The exclusion is relative, which matters more than it reads. Excluding every w:t that has a
    text box ancestor anywhere also excludes the box's own paragraphs when they are the ones
    being asked about, so each looked empty, was suppressed as blank, and the callout vanished
    exactly as before. The intersect tests that the box sits within this node rather than
    around it.
  -->
  <xsl:function name="docmd:own-text" as="xs:string">
    <xsl:param name="node" as="node()"/>
    <xsl:sequence select="string-join(
        for $n in $node//*[self::w:t or self::w:tab or self::w:br]
                          [not(ancestor::w:del)
                           and empty(ancestor::w:txbxContent intersect $node//w:txbxContent)]
        return if ($n/self::w:t) then string($n) else ' ',
        '')"/>
  </xsl:function>

  <xsl:function name="docmd:visible-text" as="xs:string">
    <xsl:param name="node" as="node()"/>
    <xsl:sequence select="string-join(
        for $n in $node//*[self::w:t or self::w:tab or self::w:br][not(ancestor::w:del)]
        return if ($n/self::w:t) then string($n) else ' ',
        '')"/>
  </xsl:function>

</xsl:stylesheet>
