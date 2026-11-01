package hentaireader.app.ui.reader

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.WindowInsetsSides
import androidx.compose.foundation.layout.displayCutout
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.only
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.union
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.FitScreen
import androidx.compose.material.icons.filled.FirstPage
import androidx.compose.material.icons.filled.LastPage
import androidx.compose.material.icons.filled.Numbers
import androidx.compose.material.icons.filled.ViewDay
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledIconToggleButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import hentaireader.app.R
import hentaireader.app.system.KeepScreenOn
import hentaireader.app.ui.common.ErrorInfo
import hentaireader.app.ui.common.ErrorPanel
import hentaireader.app.ui.common.rememberStrings
import kotlinx.coroutines.delay

/**
 * The reader screen (005 US1–US5, 007 US3; contracts/android-ui.md "Reader"): the strip takes the
 * whole screen, no navigation pane, no top bar. A tap shows the toolbar (back, title, go to page,
 * fit width, gaps, first and last page; page indicator and scrubber at the bottom); it hides
 * 3 s after the last interaction. [actions] adds the save button (US4).
 */
@Composable
fun ReaderScreen(
    vm: ReaderViewModel,
    keepScreenOn: Boolean,
    onBack: () -> Unit,
    actions: @Composable () -> Unit = {},
) {
    var toolbar by rememberSaveable { mutableStateOf(false) }
    var lastTouch by remember { mutableLongStateOf(System.currentTimeMillis()) }
    var goTo by rememberSaveable { mutableStateOf(false) }

    LaunchedEffect(toolbar, lastTouch) {
        if (toolbar) {
            delay(3_000)
            toolbar = false
        }
    }
    LifecycleEventEffect(Lifecycle.Event.ON_STOP) { vm.saveNow() }
    BackHandler {
        vm.saveNow()
        onBack()
    }
    KeepScreenOn(keepScreenOn, lastTouch)

    Box(Modifier.fillMaxSize()) {
        val session = vm.session
        val error = vm.error
        when {
            error != null -> ErrorPanel(error, onRetry = null, modifier = Modifier.align(Alignment.Center))
            session == null -> CircularProgressIndicator(Modifier.align(Alignment.Center))
            else -> StripCanvas(vm, session, onTap = {
                toolbar = !toolbar
                lastTouch = System.currentTimeMillis()
            })
        }

        session?.limitedFor?.let { retryAfter ->
            Surface(Modifier.align(Alignment.Center).padding(24.dp), tonalElevation = 6.dp, shape = MaterialTheme.shapes.large) {
                ErrorPanel(ErrorInfo("RateLimited", retryAfter.takeIf { !it.isNaN() }, vm.sourceId), onRetry = session::retryAfterLimit)
            }
        }

        AnimatedVisibility(toolbar && session != null, Modifier.align(Alignment.TopCenter), enter = fadeIn(), exit = fadeOut()) {
            TopToolbar(vm, onBack = { vm.saveNow(); onBack() }, onGoTo = { goTo = true }, actions = actions, touched = { lastTouch = System.currentTimeMillis() })
        }
        AnimatedVisibility(toolbar && session != null, Modifier.align(Alignment.BottomCenter), enter = fadeIn(), exit = fadeOut()) {
            BottomToolbar(vm, touched = { lastTouch = System.currentTimeMillis() })
        }
    }

    if (goTo) GoToPageDialog(vm.pageCount, onDismiss = { goTo = false }) { page ->
        goTo = false
        vm.jumpTo(page)
    }
}

@Composable
private fun TopToolbar(vm: ReaderViewModel, onBack: () -> Unit, onGoTo: () -> Unit, actions: @Composable () -> Unit, touched: () -> Unit) {
    Surface(Modifier.fillMaxWidth(), color = MaterialTheme.colorScheme.surface.copy(alpha = 0.92f)) {
        Row(
            Modifier.windowInsetsPadding(WindowInsets.safeDrawing.union(WindowInsets.displayCutout).only(WindowInsetsSides.Top + WindowInsetsSides.Horizontal))
                .padding(horizontal = 4.dp, vertical = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.Gallery_Back)) }
            Text(vm.title, Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleMedium)
            IconButton(onClick = { touched(); vm.jumpTo(0) }) { Icon(Icons.Filled.FirstPage, stringResource(R.string.Reader_FirstPage)) }
            IconButton(onClick = { touched(); vm.jumpTo(vm.pageCount - 1) }) { Icon(Icons.Filled.LastPage, stringResource(R.string.Reader_LastPage)) }
            IconButton(onClick = { touched(); onGoTo() }) { Icon(Icons.Filled.Numbers, stringResource(R.string.Reader_GoToPage)) }
            FilledIconToggleButton(checked = vm.atScreenWidth, onCheckedChange = { touched(); vm.fitToWidth() }) {
                Icon(Icons.Filled.FitScreen, stringResource(R.string.Reader_FitWidth))
            }
            FilledIconToggleButton(checked = vm.gaps, onCheckedChange = { touched(); vm.toggleGaps() }) {
                Icon(Icons.Filled.ViewDay, stringResource(R.string.Reader_PageGaps))
            }
            actions()
        }
    }
}

/** Page indicator "57 / 312" and the scrubber: the target page shows while dragging, the jump happens on release. */
@Composable
private fun BottomToolbar(vm: ReaderViewModel, touched: () -> Unit) {
    val strings = rememberStrings()
    val count = vm.pageCount
    var dragging by remember { mutableStateOf(false) }
    var target by remember { mutableFloatStateOf(0f) }
    val shown = if (dragging) target.toInt() else vm.currentPage
    Surface(Modifier.fillMaxWidth(), color = MaterialTheme.colorScheme.surface.copy(alpha = 0.92f)) {
        Column(
            Modifier.windowInsetsPadding(WindowInsets.safeDrawing.union(WindowInsets.displayCutout).only(WindowInsetsSides.Bottom + WindowInsetsSides.Horizontal))
                .padding(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Text(stringResource(R.string.Reader_PageOf, strings.number(shown + 1), strings.number(count)),
                Modifier.align(Alignment.CenterHorizontally), style = MaterialTheme.typography.labelLarge)
            if (count > 1) {
                Slider(
                    value = if (dragging) target else vm.currentPage.toFloat(),
                    onValueChange = { touched(); dragging = true; target = it },
                    onValueChangeFinished = { dragging = false; vm.jumpTo(target.toInt()) },
                    valueRange = 0f..(count - 1).toFloat(),
                )
            }
        }
    }
}

/** Go to page (005 FR-014): a number from 1 to the page count; anything else is rejected with a message. */
@Composable
private fun GoToPageDialog(count: Int, onDismiss: () -> Unit, onGo: (Int) -> Unit) {
    val strings = rememberStrings()
    var text by rememberSaveable { mutableStateOf("") }
    var invalid by rememberSaveable { mutableStateOf(false) }
    val submit = {
        val n = text.trim().toIntOrNull()
        if (n == null || n < 1 || n > count) invalid = true else onGo(n - 1)
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.Reader_GoToPage)) },
        text = {
            OutlinedTextField(
                value = text,
                onValueChange = { text = it; invalid = false },
                label = { Text(stringResource(R.string.Reader_GoToPage_Prompt, strings.number(count))) },
                isError = invalid,
                supportingText = { if (invalid) Text(stringResource(R.string.Reader_GoToPage_Invalid, strings.number(count))) },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
            )
        },
        confirmButton = { TextButton(onClick = submit) { Text(stringResource(R.string.Reader_Go)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.Reader_Cancel)) } },
    )
}
