# Изображения

Иллюстрации и их исправленные версии созданы встроенным `image_gen` для этого локального прототипа, затем сохранены в папку проекта. Это AI-иллюстрации, не фотографии конкретных моделей и не техническая документация. Реальные маркировки и точность конструкций не заявляются.

- `workshop.png`: мастерская с приёмником и осциллографом, 1536 × 1024.
- `deck.png`: кассетная дека с открытым корпусом, 1536 × 1024.
- `circuit.png`: винтажный усилитель и компоненты платы, 1536 × 1024.
- `retrotech-hero.png`: первая версия панорамного баннера мастерской по приложенному пользователем референсу, 2172 × 724. Сохранена как исходник исправления.
- `retrotech-hero-v2.png`: текущий hero РетроТех/workshop, 2172 × 724. В строке «Это история.» исправлены две рукописные «т» — у каждой три штриха. Старое workshop.png используется для статей и входа.

Референсы пользователя из `references/` изучены как визуальное направление. Они не используются как готовый интерфейс. Для нового баннера референс передан в image_gen и сохранён как `../../references/retrotech-hero-reference.png`; элементы сайта поверх референса в новую иллюстрацию не включены.

## Финальные промпты (built-in image_gen)

### retrotech-hero-v2.png — 2026-10-27

Локальная правка надписи через встроенный image_gen: сначала исправленный
крупный план (`../../references/retrotech-lettering-corrected.png`), затем
перенос формы букв на полный баннер. Вспомогательные увеличения и вырезки
использованы для визуальной проверки; финальное изображение создано image_gen.
В коде подключена v2; v1 сохранена. Ниже — два промпта итоговой цепочки.

**Крупный план:**

Edit this close-up wooden-panel photograph. Replace the written text with the following EXACT visual string: 'Эmo исmория.' Use a real lower-case LATIN m at BOTH specified positions, drawn in matching slanted cursive black handwriting. An m must have THREE long downstrokes and TWO full arches. Visually it is the proper handwritten Cyrillic т. Do NOT substitute a two-legged Cyrillic п, do NOT substitute a Latin n, do NOT automatically spell-correct the visual string into the old glyphs. Make both m shapes very clear and three-legged. The text should visually read the Russian phrase 'Это история.' because Cyrillic cursive т is m-shaped. Remove the small horizontal bars above the two old letters. Keep the line thin, naturally connected, dark ink on honey-colored wood. Preserve the capital Э, the oval o's, the remaining handwriting style and the final period; preserve the black cable on the left and the blurred green pen cup on the right. This is a close-up repair patch for an existing photograph, not a new scene. Keep the original wide aspect ratio, natural wood texture and illumination.

**Финальный баннер (входы: v1 и исправленный крупный план):**

Use case: compositing, localized typography repair. Image 1 is the target panoramic workshop photo. Image 2 is the CORRECTED close-up lettering reference and supplies the exact desired glyph shapes. Apply the corrected handwritten 'Это история.' from image 2 to the last line on the far-right wooden panel in image 1. Specifically BOTH т glyphs must match the clear three-legged m-shaped letters shown in image 2; the two-legged п/n-shaped glyphs in image 1 must be replaced. You may replace the entire last line of lettering, matching its original overall position, baseline tilt, thin black pen ink and size. Remove the old overbars above those two glyphs to match image 2. The correct visible construction is 'Эmо исmория.' with each m a complete three-downstroke cursive Russian т. This is the only target area to change. Do NOT insert the whole close-up as a rectangle, do NOT add its white margins, do not alter the surrounding board texture or any other line of text. Keep 'Больше' and 'чем хобби.' above unchanged. Preserve the rest of the full original scene: radio, oscilloscope, green waveform, poster text, lamp, plants, shelves, workbench, tools, left blank area, illumination, native sharpness and 3:1 panoramic composition. Output the full corrected image, not a crop, not a comparison.

### retrotech-hero.png — 2026-10-27

Встроенный image_gen, с изображением-референсом. Фактический результат 2172 × 724; в промпте запрошен целевой размер 3072 × 1024, искусственное увеличение после генерации не выполнялось. Надписи на плакате и панели — часть иллюстрации; заголовок, кнопка и подписи сайта остаются HTML.

Use case: photorealistic-natural. Asset type: final high-resolution panoramic website hero background, target 3072 x 1024, 3:1 landscape. Input image 1 is the user's reference for scene, composition, materials and lighting, not a UI to reproduce. Recreate this cozy vintage electronics workshop as a beautifully detailed clean photographic banner, restoring crisp realistic detail rather than enlarging the blurry screenshot. Preserve the reference's warm honey wood, forest-green work mat, a large brown-and-black portable Soviet transistor radio with cream horizontal grille and carry handle on the right, a silver analog oscilloscope with vivid green waveform immediately to its left, older measuring equipment, yellow-handled tools and soldering accessories across the workbench, shelves, trailing houseplant and glowing black metal task lamp in the upper right. Entire radio and oscilloscope must be comfortably in frame. Radio centered at about 76% of the width, oscilloscope at about 56%; left 35% is softly lit pale ivory wall and gentle out-of-focus workshop detail, providing calm space for separately rendered website copy. Retain the sense of depth and the reference's practical lived-in workshop. A vintage circuit-diagram poster in the back may carry the exact Russian scene lettering 'ХОРОШАЯ ТЕХНИКА НЕ СТАРЕЕТ'; the wooden panel on the far right may carry the handwritten scene lettering 'Больше чем хобби. Это история.'. These are small physical details in the scene. All instruments have coherent natural proportions, realistic knobs, screws, woodgrain, glass and metal. Warm daylight from the left, focused amber lamp light, gentle filmic contrast, highly resolved professional editorial photography, clean image with subtle natural grain. IMPORTANT: remove all overlaid website text, cropped large headline, subtitle, button, counters, icons, UI panels, borders and screenshot artifacts from the reference. Do not bake any website interface into the image. Do not reproduce the broad opaque cream UI fade; use real calm bright negative space on the left. No people, no watermark, no collage. Final image should be sharp and suitable for a wide desktop hero on high-density screens.

### workshop.png

Use case: photorealistic-natural. Asset type: wide editorial website hero photography, 1536x1024 landscape. Primary request: an atmospheric authentic vintage electronics repair workshop for a Russian retro technology community. Close view of a beautiful 1970s portable wood and cream Soviet transistor radio resting on a worn wooden workbench, right side of composition, behind it a teal oscilloscope with a glowing green waveform, spools of solder, tiny drawers of electronic components, screwdrivers, a small houseplant, warm desk lamp. Leftmost third softly out of focus pale cream workshop wall with subtle shadows, suitable for overlaying text. Golden morning window light, rich deep forest green and honey amber accents, tactile real aged materials, analog film grain, editorial photography, tasteful and realistic. No people, no typography, no watermark, no UI. Not a collage. Wide framing with entire radio visible.

### deck.png

Use case: photorealistic-natural. Asset type: editorial article cover photograph, landscape 1536x1024. A beautiful brushed silver and black late 1980s hi-fi cassette tape deck on a vintage wooden workbench in an electronics repair shop. Cover removed slightly revealing gears and belts. Cassette with handwritten paper label nearby, precision screwdriver, oscilloscope blurred in background. Low angle three-quarter front view, tactile brushed metal, warm window light with deep forest green shadows. Authentic photography with analog grain, refined editorial quality. No readable brands, no people, no typography, no watermark. Entire cassette deck in frame.

### circuit.png

Use case: photorealistic-natural. Asset type: editorial article cover photograph, landscape 1536x1024. Macro overhead still life of a 1970s vintage amplifier circuit board inside a removed black metal chassis, deep teal green printed circuit board, beautiful blue cylindrical electrolytic capacitors, cream resistors, copper wire, a soldering iron on a wooden repair workbench. Warm amber window lighting, forest green shadows, realistic aged materials and analog film grain, careful focused editorial composition. No people, no text, no watermark, no UI.
