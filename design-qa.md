# Design QA — received-letter paper and handwriting

## Evidence

- Source visual truth: `preview/letter-share-ZzWjzbgK-reply-paper.png`
- Final implementation screenshot: `preview/chillzhuo-implementation-final-full.png`
- Focused implementation crop: `preview/chillzhuo-implementation-final-letter.png`
- Side-by-side comparison: `preview/chillzhuo-reference-final-comparison.png`
- Full-screen interaction evidence from the previous layout: `preview/fullscreen-letter-scrolled.png` (superseded by the centered-overlay revision)
- State: first page of a received letter, reply paper visible, normal Windows display scale
- Source pixels: 660 × 372 at 1× density
- Implementation window pixels: 1114 × 907 at 1× density
- Implementation paper crop: 559 × 313 at 1× density
- Normalization: the paper crop was scaled to 660 × 370 and placed beside the 660 × 372 source; no sharpening or color correction was applied

The source and implementation contain different letter copy, so exact glyph positions and line wrapping are not treated as pixel-match evidence. The comparison is limited to the user-requested visual surfaces: handwriting character, ink weight, line rhythm, margins, paper color, border, and date treatment.

The previous Release UI was opened in a real WPF window and verified with a separate `PaperBackground` image and `ReplyScroll` text pane. The centered-overlay revision was compiled but its new visual state was not recaptured because the user stopped Computer Use with the physical Escape key.

## Required fidelity surfaces

- Fonts and typography: ChillZhuo v2.500 has the source's loose, irregular handwritten character much more closely than Xiaolai. Body text remains 18 px with 23 px line height. A 0.22 px horizontal overdraw restores the source-like ink density without making the strokes look typeset or bold. There is no truncation or ellipsis in received-letter body text.
- Spacing and layout rhythm: the 4.2% left/top body inset, 91.6% text width, paragraph gaps, and bottom-right date placement visually follow the source. The normalized paper aspect ratio differs by less than one percent.
- Colors and visual tokens: the warm off-white paper, low-contrast fibers, dark brown-black ink, and faint ornamental border remain consistent with the source direction.
- Image quality and asset fidelity: the implementation uses the generated paper bitmap at a 2× WPF render scale. The comparison shows no stretching artifacts, transparency halos, or blurred type.
- Copy and content: the source and local history necessarily contain different letters and dates. Content semantics are intentionally unchanged; only presentation was evaluated.

## Findings

- No actionable P0, P1, or P2 mismatch remains for the requested handwriting change.
- [P3] The source sample has slightly more per-glyph pressure variation than any reusable font can reproduce. This is acceptable because the chosen font preserves the same informal handwriting direction without copying the original font asset.

## Comparison history

1. Xiaolai Regular: blocked because the glyphs were too square, heavy, and regular.
2. ChillZhuo v2.500, first pass: the handwriting direction matched, but the implementation ink looked slightly lighter than the source.
3. ChillZhuo v2.500, final pass: added a 0.22 px horizontal overdraw to body, page marker, title, and date rendering. The final side-by-side evidence shows the weight difference reduced without losing the irregular stroke character.

## Implementation checklist

- [x] Replace Xiaolai/Yozai distribution references with ChillZhuo v2.500.
- [x] Preserve the official attribution and usage statement beside the font.
- [x] Build the Release configuration with zero warnings and zero errors.
- [x] Capture the real WPF window and compare the paper region at normalized size.
- [ ] Recapture the centered-overlay full-screen mode and verify text-only scrolling after the user reopens the Release executable.
- [x] Confirm no P0/P1/P2 visual mismatch remains.

## Follow-up polish

- P3 only: if a future sample must imitate changing pen pressure more closely, use per-letter raster handwriting rather than globally thickening the reusable font.

final result: blocked

Blocker: the user stopped the Windows Computer Use verification before the centered-overlay revision could be captured.
