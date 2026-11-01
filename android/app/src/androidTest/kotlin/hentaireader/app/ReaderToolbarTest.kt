package hentaireader.app

import androidx.compose.ui.test.ExperimentalTestApi
import androidx.compose.ui.test.SemanticsNodeInteractionsProvider
import androidx.compose.ui.test.assertIsNotDisplayed
import androidx.compose.ui.test.click
import androidx.compose.ui.test.hasContentDescription
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onFirst
import androidx.compose.ui.test.onRoot
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.swipeRight
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import hentaireader.host.Preferences
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertNotEquals
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The reader's shell (007 T054, contracts/android-ui.md "Reader"): a tap toggles the toolbar, the
 * drawer cannot be opened in the reader, Back returns to the gallery. Uses the live E-Hentai front
 * page (the instrumented app has no fixture transport), so it needs the network; not a CI gate.
 */
@OptIn(ExperimentalTestApi::class)
@RunWith(AndroidJUnit4::class)
class ReaderToolbarTest {
    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    private val context = InstrumentationRegistry.getInstrumentation().targetContext
    private fun text(id: Int) = context.getString(id)

    @Before
    fun confirmed() {
        runBlocking {
            context.services.updatePrefs {
                it.copy(adultConfirmed = true, startPage = Preferences.START_SOURCE_PREFIX + "ehentai", defaultQueries = emptyMap())
            }
        }
        context.services.start()
        rule.activityRule.scenario.recreate()
    }

    private fun SemanticsNodeInteractionsProvider.exists(matcher: androidx.compose.ui.test.SemanticsMatcher) =
        onAllNodes(matcher).fetchSemanticsNodes().isNotEmpty()

    @Test
    fun tapTogglesToolbarDrawerStaysClosedBackReturnsToGallery() {
        // A result card, its gallery, then Read.
        val card = hasText(" pages", substring = true)
        rule.waitUntil(30_000) { rule.exists(card) }
        rule.onAllNodes(card).onFirst().performClick()
        val read = hasText(text(R.string.Gallery_Read)) or hasText(text(R.string.Gallery_Continue))
        rule.waitUntil(30_000) { rule.exists(read) }
        rule.onAllNodes(read).onFirst().performClick()

        // In the reader: the go-to-page button belongs to its toolbar.
        val toolbar = hasContentDescription(text(R.string.Reader_GoToPage))
        rule.waitUntil(15_000) { rule.exists(hasContentDescription(text(R.string.Reader_FirstPage))) || !rule.exists(read) }
        val before = rule.exists(toolbar)
        rule.onRoot().performTouchInput { click(center) }
        rule.waitUntil(5_000) { rule.exists(toolbar) != before }
        assertNotEquals("a tap toggles the toolbar", before, rule.exists(toolbar))

        // A swipe from the left edge does not open the drawer.
        rule.onRoot().performTouchInput { swipeRight(startX = 2f, endX = width * 0.8f) }
        rule.waitForIdle()
        // The drawer's content stays composed while closed, so it must not be displayed.
        rule.onNode(hasText(text(R.string.Nav_Settings))).assertIsNotDisplayed()

        // Back returns to the gallery.
        rule.runOnUiThread { rule.activity.onBackPressedDispatcher.onBackPressed() }
        rule.waitUntil(10_000) { rule.exists(read) }
    }
}
