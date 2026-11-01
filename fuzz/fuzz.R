# Fuzzes the user-facing functions of openxlsx2 and encharter: every case is a small
# valid workbook plus one call with fuzzed arguments. Calls that stop with an error
# rejected their input, which is fine. Calls that are accepted but write a file the
# validator reports as invalid are findings.
#
# usage: Rscript fuzz/fuzz.R [--target=openxlsx2|encharter|all] [--n=500] [--seed=1]
#                            [--validator=path] [--out=dir] [--keep]

suppressPackageStartupMessages(library(openxlsx2))

`%||%` <- function(x, y) if (is.null(x)) y else x

args <- commandArgs(trailingOnly = TRUE)
opt <- list(target = "all", n = "500", seed = "1", validator = "", out = "", keep = "FALSE")
for (a in args) {
  kv <- strsplit(sub("^--", "", a), "=", fixed = TRUE)[[1]]
  opt[[kv[1]]] <- if (length(kv) > 1) paste(kv[-1], collapse = "=") else "TRUE"
}
n_cases <- as.integer(opt$n)
set.seed(as.integer(opt$seed))

script_dir <- dirname(normalizePath(sub("^--file=", "", grep("^--file=", commandArgs(FALSE), value = TRUE)[1])))
repo_dir <- dirname(script_dir)

validator <- opt$validator
if (!nzchar(validator)) validator <- Sys.getenv("OOXMLVALIDATOR")
if (!nzchar(validator)) {
  bins <- list.files(file.path(repo_dir, "bin"), pattern = "^OOXMLValidator(\\.exe)?$",
                     recursive = TRUE, full.names = TRUE)
  bins <- bins[grepl("/publish/", bins, fixed = TRUE)]
  if (length(bins)) validator <- bins[order(file.mtime(bins), decreasing = TRUE)][1]
}
if (!nzchar(validator) || !file.exists(validator)) stop("validator not found, use --validator=path", call. = FALSE)

out_dir <- if (nzchar(opt$out)) opt$out else file.path(tempdir(), "ooxml-fuzz")
unlink(out_dir, recursive = TRUE)
dir.create(out_dir, recursive = TRUE, showWarnings = FALSE)
ignore_files <- c(file.path(repo_dir, "excel-compat.ignore"), file.path(script_dir, "known.ignore"))

# value generators -------------------------------------------------------------

pick <- function(x) x[[sample.int(length(x), 1)]]

g_choice <- function(valid, invalid) {
  function() if (runif(1) < 0.8) pick(valid) else pick(invalid)
}

g_string <- function() {
  ctrl <- intToUtf8(sample(c(1:8, 11, 12, 14:31), 1))
  pick(list(
    "plain", "Some Text", "", " leading and trailing ", "a\nb", "a\r\nb", "tab\there",
    "<b>&amp; \"quoted\" 'single'</b>", "]]>", "&#1;", "_x0001_",
    "\u00e4\u00f6\u00fc \u00df \u20ac \u4e2d\u6587", "\U0001F600 emoji",
    paste0("ctrl", ctrl, "char"), "\uFFFE", "\uFFFF",
    strrep("x", 255), strrep("x", 256), strrep("y", 32768),
    NA_character_, "=SUM(A1)", "1e5", "TRUE"
  ))
}

g_number <- function() {
  pick(list(0, 1, -1, 0.5, 2.75, 10, 72, 255, 256, 400, 409, 410, 1e10, -1e10,
            1e-10, .Machine$integer.max, NA, NaN, Inf, -Inf, 3L))
}

g_bool <- function() pick(list(TRUE, FALSE, NA, 1, "yes"))

g_dims <- function() {
  pick(list("A1", "B2:D4", "A1:A1", "C3", "XFD1048576", "XFE1",
            "A1048577", "a1", "B2:A1", "$A$1", "A1,B2", "A1;B2", "", "R1C1", "A0"))
}

g_range <- function() pick(list(g_dims(), "A:A", "1:1", "A1:XFD1048576"))

g_cell <- function() pick(list("A1", "B2", "XFD1048576", "XFE1", "A0", "a1", ""))

g_cols <- function() pick(list(1, 2:3, 16384, 16385, 0, -1, "A", c("B", "D"), NA))

g_rows <- function() pick(list(1, 2:3, 1048576, 1048577, 0, -1, NA))

g_color <- function() {
  pick(list(
    quote(wb_color(name = "red")), quote(wb_color(name = "notacolor")),
    quote(wb_color(hex = "FF00FF00")), quote(wb_color(hex = "00FF00")),
    quote(wb_color(hex = "#00FF00")), quote(wb_color(hex = "zz")),
    quote(wb_color(hex = "FFFFFFFFFF")), quote(wb_color(theme = 4)),
    quote(wb_color(theme = 99)), quote(wb_color(theme = 4, tint = 0.5)),
    quote(wb_color(theme = 4, tint = 2)), quote(wb_color(indexed = 8)),
    quote(wb_color(indexed = 200)), quote(wb_color(auto = TRUE)),
    "FF0000", "red", "#123456", "zz"
  ))
}

g_sheetname <- function() {
  pick(list("Sheet 2", "data", strrep("n", 31), strrep("n", 32), "a[b]", "a:b", "a*b",
            "a?b", "a/b", "a\\b", "'quoted'", "History", "", "\u00e4\u00f6\u00fc", "x\ny",
            g_string()))
}

g_name <- function() {
  pick(list("my_name", "Table1", "A1", "R1C1", "1abc", "has space", "_ok", "x.y",
            strrep("n", 255), strrep("n", 256), "\u00e4rger", "", g_string()))
}

g_data <- function() {
  n <- sample(1:3, 1)
  pick(list(
    data.frame(s = vapply(seq_len(n), function(i) g_string(), ""), stringsAsFactors = FALSE),
    data.frame(v = unlist(lapply(seq_len(n), function(i) g_number()))),
    data.frame(d = as.Date(c("1899-12-30", "1900-01-01", "1800-01-01", NA))[seq_len(n)]),
    data.frame(t = as.POSIXct(c("2026-10-31 12:00:00", "1960-01-01 00:00:00", NA), tz = "UTC")[seq_len(n)]),
    data.frame(f = factor(c("a", "\u00e4", NA))[seq_len(n)]),
    data.frame(l = c(TRUE, FALSE, NA)[seq_len(n)]),
    matrix(1:4, 2),
    1:3,
    g_string()
  ))
}

g_hf <- function() {
  pick(list(c("left", "center", "right"), c("&[Page]", "&[Date]", "&[File]"),
            c(NA, "only center", NA), c(g_string(), g_string(), g_string()), g_string()))
}

g_formula <- function() {
  pick(list("SUM(A1:A3)", "=SUM(A1:A3)", "A1&\"<&>\"", "1/0", "SUIM(A1)", "_xlfn.SEQUENCE(3)",
            "SEQUENCE(3)", g_string()))
}

g_url <- function() {
  pick(list("https://example.com", "https://example.com/a b?x=1&y=2", "mailto:a@b.c",
            "file:///C:/tmp/x.xlsx", "#'Sheet 1'!A1", "http://[bad", "../rel/path.xlsx", g_string()))
}

g_numfmt <- function() {
  pick(list("0.00", "#,##0", "[Red]0;[Blue]-0", "\"text\"@", "yyyy-mm-dd", 4, 164, 9999, -1,
            "General", g_string()))
}

g_comment <- function() {
  pick(list(
    g_string(),
    as.call(list(as.name("wb_comment"), text = g_string(), author = g_string())),
    as.call(list(as.name("wb_comment"), text = "x", author = "a", width = g_number(), height = g_number())),
    as.call(list(as.name("wb_comment"), text = "x", author = "a", visible = g_bool()))
  ))
}

g_dv_value <- function() pick(list(1, c(1, 10), "\"a,b,c\"", "$A$1:$A$3", "A1>0", g_string(), g_number()))

# openxlsx2 targets ----------------------------------------------------------

align_h <- g_choice(list("left", "center", "right", "fill", "justify", "centerContinuous", "distributed", "general"),
                    list("middle", "above", ""))
align_v <- g_choice(list("top", "center", "bottom", "justify", "distributed"), list("above", "middle", ""))
border_style <- g_choice(list("thin", "medium", "thick", "double", "hair", "dashDot", "dotted", "none"),
                         list("wavy", "", NA))

targets_openxlsx2 <- list(
  wb_add_worksheet = list(fixed = list(), params = list(
    sheet = g_sheetname, tab_color = g_color, zoom = g_number, grid_lines = g_bool,
    visible = g_choice(list("true", "false", "hidden", "visible", "veryhidden", TRUE, FALSE), list("maybe", NA)),
    paper_size = g_number, orientation = g_choice(list("portrait", "landscape"), list("diagonal")),
    header = g_hf, footer = g_hf, hdpi = g_number)),
  wb_add_data = list(fixed = list(sheet = 1), params = list(
    x = g_data, dims = g_dims, col_names = g_bool, row_names = g_bool, na = g_string,
    inline_strings = g_bool, name = g_name, with_filter = g_bool)),
  wb_add_formula = list(fixed = list(sheet = 1), params = list(
    x = g_formula, dims = g_dims, array = g_bool, shared = g_bool, name = g_name)),
  wb_add_comment = list(fixed = list(sheet = 1), params = list(dims = g_cell, comment = g_comment)),
  wb_add_font = list(fixed = list(sheet = 1, dims = "A1:B2"), params = list(
    name = g_string, color = g_color, size = g_number, bold = g_bool, italic = g_bool,
    underline = g_choice(list("single", "double", "singleAccounting", "doubleAccounting", "none"), list("triple", "")),
    strike = g_bool, vert_align = g_choice(list("superscript", "subscript", "baseline"), list("middle", "")),
    family = g_number, charset = g_number,
    scheme = g_choice(list("major", "minor", "none"), list("x", "")))),
  wb_add_fill = list(fixed = list(sheet = 1, dims = "A1:B2"), params = list(
    color = g_color, pattern = g_choice(list("solid", "gray125", "darkGrid", "lightTrellis", "none"), list("zigzag", "")),
    every_nth_col = g_number, every_nth_row = g_number)),
  wb_add_border = list(fixed = list(sheet = 1, dims = "A1:C3"), params = list(
    bottom_color = g_color, top_color = g_color, bottom_border = border_style, top_border = border_style,
    left_border = border_style, inner_hgrid = border_style, inner_vcolor = g_color,
    diagonal_up = border_style, diagonal_color = g_color)),
  wb_add_numfmt = list(fixed = list(sheet = 1, dims = "A1:B2"), params = list(numfmt = g_numfmt)),
  wb_add_cell_style = list(fixed = list(sheet = 1, dims = "A1:B2"), params = list(
    horizontal = align_h, vertical = align_v, text_rotation = g_number, indent = g_number,
    wrap_text = g_bool, shrink_to_fit = g_bool, locked = g_bool, hidden = g_bool,
    reading_order = g_number, num_fmt_id = g_number)),
  wb_set_col_widths = list(fixed = list(sheet = 1), params = list(cols = g_cols, widths = g_number, hidden = g_bool)),
  wb_set_row_heights = list(fixed = list(sheet = 1), params = list(rows = g_rows, heights = g_number, hidden = g_bool)),
  wb_merge_cells = list(fixed = list(sheet = 1), params = list(dims = g_dims)),
  wb_add_data_validation = list(fixed = list(sheet = 1, value = 1), params = list(
    dims = g_range,
    type = g_choice(list("whole", "decimal", "date", "time", "textLength", "list", "custom", "any"), list("number")),
    operator = g_choice(list("between", "notBetween", "equal", "notEqual", "greaterThan", "lessThan",
                             "greaterThanOrEqual", "lessThanOrEqual"), list("approx")),
    value = g_dv_value, allow_blank = g_bool,
    error_style = g_choice(list("stop", "warning", "information"), list("fatal")),
    error_title = g_string, error = g_string, prompt_title = g_string, prompt = g_string)),
  wb_add_conditional_formatting = list(fixed = list(sheet = 1), params = list(
    dims = g_range,
    type = g_choice(list("expression", "colorScale", "dataBar", "iconSet", "duplicatedValues", "uniqueValues",
                         "containsErrors", "containsBlanks", "containsText", "beginsWith", "endsWith",
                         "between", "topN", "bottomN"), list("bogus")),
    rule = function() pick(list("A1>1", ">2", "a", c(1, 2), c("red", "blue"), g_string(), g_number())))),
  wb_add_named_region = list(fixed = list(sheet = 1), params = list(
    dims = g_range, name = g_name, comment = g_string, hidden = g_bool, local_sheet = g_bool,
    description = g_string)),
  wb_add_data_table = list(fixed = list(sheet = 1, dims = "E1"), params = list(
    x = g_data, table_name = g_name, total_row = g_bool, banded_rows = g_bool,
    table_style = g_choice(list("TableStyleMedium2", "TableStyleLight1", "none"), list("TableStyleLight99", "")))),
  wb_set_properties = list(fixed = list(), params = list(
    creator = g_string, title = g_string, subject = g_string, category = g_string, keywords = g_string,
    comments = g_string, manager = g_string, company = g_string, modifier = g_string,
    datetime_created = function() pick(list(Sys.time(), as.POSIXct("1899-12-31", tz = "UTC"), "2026-10-31", NA)))),
  wb_protect = list(fixed = list(protect = TRUE), params = list(
    password = g_string, lock_structure = g_bool, lock_windows = g_bool, file_sharing = g_bool,
    username = g_string, read_only_recommended = g_bool)),
  wb_protect_worksheet = list(fixed = list(sheet = 1), params = list(
    password = g_string,
    properties = g_choice(list("selectLockedCells", "formatCells", c("insertRows", "deleteRows")), list("bogus")))),
  wb_set_header_footer = list(fixed = list(sheet = 1), params = list(
    header = g_hf, footer = g_hf, even_header = g_hf, first_footer = g_hf, align_with_margins = g_bool)),
  wb_page_setup = list(fixed = list(sheet = 1), params = list(
    orientation = g_choice(list("portrait", "landscape"), list("diagonal")), scale = g_number, left = g_number,
    top = g_number, header = g_number, fit_to_width = g_number, fit_to_height = g_number, paper_size = g_number,
    print_title_rows = g_rows, print_title_cols = g_cols)),
  wb_freeze_pane = list(fixed = list(sheet = 1), params = list(
    first_active_row = g_number, first_active_col = g_number, first_row = g_bool, first_col = g_bool)),
  wb_add_hyperlink = list(fixed = list(sheet = 1), params = list(
    dims = g_cell, target = g_url, tooltip = g_string, is_external = g_bool)),
  wb_set_base_font = list(fixed = list(), params = list(font_size = g_number, font_color = g_color, font_name = g_string)),
  wb_set_sheetview = list(fixed = list(sheet = 1), params = list(
    zoom_scale = g_number, right_to_left = g_bool, show_grid_lines = g_bool, top_left_cell = g_cell,
    view = g_choice(list("normal", "pageLayout", "pageBreakPreview"), list("fancy")), color_id = g_number,
    show_zeros = g_bool)),
  wb_add_form_control = list(fixed = list(sheet = 1), params = list(
    dims = g_cell, type = g_choice(list("Checkbox", "Radio", "Drop"), list("Button")), text = g_string,
    link = g_cell, range = g_dims, checked = g_bool)),
  wb_set_sheet_names = list(fixed = list(old = 1), params = list(new = g_sheetname)),
  wb_add_filter = list(fixed = list(sheet = 1, rows = 1), params = list(cols = g_cols)),
  wb_set_grid_lines = list(fixed = list(sheet = 1), params = list(show = g_bool, print = g_bool)),
  wb_add_ignore_error = list(fixed = list(sheet = 1), params = list(
    dims = g_range, number_stored_as_text = g_bool, formula = g_bool, eval_error = g_bool)),
  wb_add_sparklines = list(fixed = list(sheet = 1), params = list(
    sparklines = function() as.call(c(as.name("create_sparklines"), list(
      sheet = "Sheet 1", dims = pick(list("A2:A4", "A2:B4", "XFE1", "")), sqref = pick(list("D2", "D2:D3", "A0")),
      type = pick(list(NULL, "column", "stacked", "line", "bogus")), color_series = g_color(),
      line_weight = g_number(), high = g_bool())))))
)


image_file <- file.path(tempdir(), "fuzz-image.png")
if (requireNamespace("png", quietly = TRUE)) png::writePNG(array(0.5, c(4, 4, 3)), image_file)

g_visibility <- g_choice(list("visible", "hidden", "veryHidden", TRUE, FALSE), list("bogus", NA))
pivot_pre <- list(quote(wb <- wb_add_pivot_table(wb, x = wb_data(wb, 1), rows = "b", data = "a", pivot_table = "pt1")))

targets_openxlsx2 <- c(targets_openxlsx2, list(
  # a chartsheet needs a chart
  wb_add_chartsheet = list(fixed = list(), params = list(
    sheet = g_sheetname, tab_color = g_color, zoom = g_number, visible = g_visibility),
    post = if (requireNamespace("encharter", quietly = TRUE)) list(quote(wb <- wb_add_encharter(
      wb, graph = encharter::ec("barChart")$add_series(data = "'Sheet 1'!$A$2:$A$4"))))),
  wb_add_creators = list(fixed = list(), params = list(creators = g_string)),
  wb_set_last_modified_by = list(fixed = list(), params = list(name = g_string)),
  wb_add_dxfs_style = list(fixed = list(), params = list(
    name = g_name, font_name = g_string, font_size = g_number, font_color = g_color, num_fmt = g_numfmt,
    border = g_bool, border_color = g_color, border_style = border_style, bg_fill = g_color,
    text_bold = g_bool, text_underline = g_bool)),
  wb_add_named_style = list(fixed = list(sheet = 1, dims = "A1"), params = list(
    name = g_choice(list("Good", "Bad", "Neutral", "Title", "Heading 1", "Note"), list("bogus", g_string())),
    font_name = g_string, font_size = g_number)),
  wb_add_page_break = list(fixed = list(sheet = 1), params = list(row = g_rows, col = g_cols)),
  wb_add_person = list(fixed = list(), params = list(name = g_string, id = g_string, user_id = g_string, provider_id = g_string)),
  wb_add_thread = list(fixed = list(sheet = 1),
    pre = list(quote(wb <- wb_add_person(wb, name = "Someone"))),
    params = list(dims = g_cell, comment = g_string, reply = g_bool, resolve = g_bool,
                  person_id = function() pick(list(quote(wb_get_person(wb)$id[1]), "bogus", g_string())))),
  wb_add_pivot_table = list(fixed = list(x = quote(wb_data(wb, 1)), rows = "b", data = "a"), params = list(
    dims = g_dims, pivot_table = g_name, sheet = function() pick(list(1, "pivot", g_sheetname())),
    fun = g_choice(list("sum", "count", "average", "max", "min", "product", "stdDev", "var"), list("median", "")),
    params = function() pick(list(list(name = g_name()), list(numfmt = g_numfmt()), list(sort_item = g_number()),
                                  list(compact = g_bool()), list(apply_number_formats = g_bool()),
                                  list(row_grand_totals = g_bool()), list(choose = c(b = g_string()))))))
  ,
  wb_add_slicer = list(fixed = list(x = quote(wb_data(wb, 1)), slicer = "b", pivot_table = "pt1"), pre = pivot_pre,
    params = list(dims = g_dims, sheet = function() pick(list(1, 2, "Sheet 1")),
                  params = function() pick(list(list(caption = g_string()), list(style = g_string()),
                                                list(sort_order = pick(list("ascending", "descending", "bogus"))),
                                                list(column_count = g_number()), list(row_height = g_number()),
                                                list(level = g_number()))))),
  wb_add_timeline = list(fixed = list(x = quote(wb_data(wb, 1)), timeline = "d", pivot_table = "pt1"),
    pre = list(quote(wb <- wb_add_data(wb, x = data.frame(d = as.Date("2026-01-01") + 0:2), dims = "C1")),
               quote(wb <- wb_add_pivot_table(wb, x = wb_data(wb, 1), rows = "d", data = "a", pivot_table = "pt1"))),
    params = list(dims = g_dims, params = function() pick(list(list(caption = g_string()), list(style = g_string()),
                                                              list(level = g_number()),
                                                              list(show_header = g_bool()))))),
  wb_clone_worksheet = list(fixed = list(old = 1), params = list(new = g_sheetname)),
  wb_copy_cells = list(fixed = list(sheet = 1, data = quote(wb_data(wb, 1))), params = list(
    dims = g_dims, as_value = g_bool, as_ref = g_bool, transpose = g_bool)),
  wb_group_cols = list(fixed = list(sheet = 1), params = list(cols = g_cols, collapsed = g_bool, levels = g_number)),
  wb_group_rows = list(fixed = list(sheet = 1), params = list(rows = g_rows, collapsed = g_bool, levels = g_number)),
  wb_set_bookview = list(fixed = list(), params = list(
    active_tab = g_number, first_sheet = g_number, minimized = g_bool, tab_ratio = g_number,
    visibility = g_visibility, window_height = g_number, window_width = g_number, x_window = g_number,
    show_sheet_tabs = g_bool, auto_filter_date_grouping = g_bool)),
  wb_set_sheet_visibility = list(fixed = list(),
    pre = list(quote(wb <- wb_add_worksheet(wb, "second"))),
    params = list(sheet = function() pick(list(1, 2, c(1, 2), "second", 3)), value = g_visibility)),
  wb_set_order = list(fixed = list(), pre = list(quote(wb <- wb_add_worksheet(wb, "second"))),
    params = list(sheets = function() pick(list(c(2, 1), c(1, 1), 3, c("second", "Sheet 1"), 0)))),
  wb_set_selected = list(fixed = list(), pre = list(quote(wb <- wb_add_worksheet(wb, "second"))),
    params = list(sheet = function() pick(list(1, 2, "second", 3, c(1, 2))))),
  wb_set_active_sheet = list(fixed = list(), pre = list(quote(wb <- wb_add_worksheet(wb, "second"))),
    params = list(sheet = function() pick(list(1, 2, "second", 3)))),
  wb_set_base_colors = list(fixed = list(), params = list(
    theme = g_choice(list("Office", "Office 2013 - 2022", "Aspect", "Office 2007 - 2010"), list("bogus", 99)))),
  wb_add_image = list(fixed = list(sheet = 1, file = image_file), params = list(
    dims = g_dims, width = g_number, height = g_number, row_offset = g_number, col_offset = g_number,
    units = g_choice(list("in", "cm", "px"), list("mm")), dpi = g_number, address = g_url)),
  wb_update_table = list(fixed = list(sheet = 1),
    pre = list(quote(wb <- wb_add_data_table(wb, x = data.frame(p = 1:2, q = 3:4), dims = "E1", table_name = "tab_one"))),
    params = list(dims = g_dims, tabname = function() pick(list("tab_one", "bogus", g_string())))),
  wb_add_style_cell = list(fn = "wb_add_style", fixed = list(), params = list(
    style = function() as.call(c(as.name("create_cell_style"), draw_args(list(
      horizontal = align_h, vertical = align_v, indent = g_number, text_rotation = g_number,
      wrap_text = g_bool, shrink_to_fit = g_bool, locked = g_bool, hidden = g_bool, num_fmt_id = g_number,
      reading_order = g_number)))),
    style_name = g_name)),
  wb_add_style_font = list(fn = "wb_add_style", fixed = list(), params = list(
    style = function() as.call(c(as.name("create_font"), draw_args(list(
      name = g_string, sz = g_number, b = g_bool, color = g_color, family = g_number, charset = g_number,
      u = g_choice(list("single", "double"), list("triple")), scheme = g_choice(list("major", "minor"), list("x")))))),
    style_name = g_name)),
  wb_add_style_border = list(fn = "wb_add_style", fixed = list(), params = list(
    style = function() as.call(c(as.name("create_border"), draw_args(list(
      bottom = border_style, top = border_style, left = border_style, bottom_color = g_color,
      diagonal = border_style, diagonal_up = g_bool, outline = g_bool)))),
    style_name = g_name)),
  wb_add_style_fill = list(fn = "wb_add_style", fixed = list(), params = list(
    style = function() as.call(c(as.name("create_fill"), draw_args(list(
      pattern_type = g_choice(list("solid", "gray125", "none"), list("zigzag")), fg_color = g_color, bg_color = g_color)))),
    style_name = g_name)),
  wb_add_style_numfmt = list(fn = "wb_add_style", fixed = list(), params = list(
    style = function() call("create_numfmt", numFmtId = pick(list(164, 4, -1, 1e6, NA)), formatCode = g_numfmt()),
    style_name = g_name)),
  wb_set_cell_style_across = list(fixed = list(sheet = 1, style = "A1"), params = list(cols = g_cols, rows = g_rows))
))

# encharter targets ----------------------------------------------------------

chart_types <- c("barChart", "lineChart", "areaChart", "scatterChart", "pieChart", "doughnutChart",
                 "ofPieChart", "bar3DChart", "line3DChart", "pie3DChart", "area3DChart", "surface3DChart",
                 "bubbleChart", "radarChart", "stockChart")
chartex_types <- c("waterfall", "treemap", "sunburst", "funnel", "boxWhisker")

legend_pos <- g_choice(list("r", "l", "t", "b", "tr"), list("x", ""))
line_type <- g_choice(list("solid", "dash", "sysDot", "lgDashDot"), list("zigzag"))
text_params <- list(text = g_string, font_size = g_number, font_name = g_string, font_color = g_color,
                    bold = g_bool, italic = g_bool, fill = g_color, line = g_color, line_width = g_number)
axis_params <- list(min = g_number, max = g_number, major = g_number, minor = g_number, format = g_numfmt,
                    log_base = g_number, rev = g_bool, color = g_color, font_size = g_number,
                    font_name = g_string, rotation = g_number, grid_color = g_color, grid_lines = g_bool,
                    line_width = g_number)

targets_encharter <- list(
  add_series = list(chartex = FALSE, params = list(
    name = function() pick(list("data!$B$1", "plain name", g_string())), color = g_color,
    line_width = g_number, line_type = line_type,
    marker = g_choice(list("circle", "square", "none", "diamond", "triangle"), list("heart")),
    marker_size = g_number, gap_width = g_number, overlap = g_number, smooth = g_bool,
    show_val = g_bool, show_cat = g_bool, line_color = g_color, invert_if_negative = g_bool,
    label = function() pick(list("data!$C$2:$C$4", "C2:C4", "data!$Z$1", g_string())),
    error_bars = function() list(type = pick(list("fixedVal", "percentage", "stdDev", "stdErr", "bogus")), value = g_number()),
    trendline = function() list(type = pick(list("linear", "exp", "log", "poly", "movingAvg", "bogus")),
                                order = g_number(), period = g_number()))),
  set_chart_title = list(chartex = NA, params = text_params),
  set_x_title = list(chartex = FALSE, params = text_params),
  set_y_axis = list(chartex = FALSE, params = axis_params),
  set_x_axis = list(chartex = FALSE, params = axis_params),
  set_legend_style = list(chartex = NA, params = list(
    pos = legend_pos, align = g_choice(list("ctr", "min", "max"), list("left")), overlay = g_bool,
    font_size = g_number, font_name = g_string, bold = g_bool, color = g_color)),
  set_data_label_style = list(chartex = FALSE, params = list(
    show_val = g_bool, show_cat = g_bool, show_legend_key = g_bool, show_ser_name = g_bool,
    show_percent = g_bool, format = g_numfmt,
    pos = g_choice(list("ctr", "outEnd", "inEnd", "inBase", "bestFit", "l", "r", "t", "b"), list("middle")))),
  set_chart_style = list(chartex = NA, params = list(fill = g_color, line = g_color, line_width = g_number)),
  set_plot_style = list(chartex = FALSE, params = list(fill = g_color, line = g_color, line_width = g_number)),
  set_pie_options = list(chartex = FALSE, params = list(rotation = g_number, expansion = g_number, hole_size = g_number)),
  set_3d_options = list(chartex = FALSE, params = list(
    rot_x = g_number, rot_y = g_number, perspective = g_number, depth_percent = g_number, h_percent = g_number,
    gap_depth = g_number, shape = g_choice(list("box", "cylinder", "cone", "pyramid"), list("sphere")))),
  add_series_ex = list(chartex = TRUE, params = list(
    name = function() pick(list("data!$B$1", "plain name", g_string())), color = g_color,
    line_color = g_color, line_width = g_number, gap_width = g_number,
    label = function() pick(list("data!$C$2:$C$4", "data!$Z$1", g_string())),
    subtotals = function() pick(list(TRUE, 1, c(1, 3), 99, -1)))),
  set_waterfall_colors = list(chartex = TRUE, params = list(increase = g_color, decrease = g_color, total = g_color)),
  wb_add_encharter = list(chartex = NA, params = list(dims = g_dims, sheet = function() pick(list(1, "data", 2)))),
  update_series = list(chartex = NA, params = list(
    index = function() pick(list(1, 2, 0, -1, NA)), name = function() pick(list("data!$B$1", g_string())),
    color = g_color, line_width = g_number, marker_size = g_number, gap_width = g_number,
    label = function() pick(list("data!$C$2:$C$4", "C2:C4", g_string())))),
  set_of_pie_options = list(chartex = FALSE, type = "ofPieChart", params = list(
    type = g_choice(list("pie", "bar"), list("donut")), second_size = g_number,
    split_type = g_choice(list("auto", "cust", "percent", "pos", "val"), list("bogus")), split_pos = g_number)),
  set_bubble_options = list(chartex = FALSE, type = "bubbleChart", params = list(
    scale = g_number, show_neg = g_bool, size_represents = g_choice(list("area", "w"), list("volume")))),
  set_disp_blanks = list(chartex = FALSE, params = list(val = g_choice(list("gap", "span", "zero"), list("bogus", 0)))),
  set_plot_vis_only = list(chartex = FALSE, params = list(val = g_bool)),
  set_data_table = list(chartex = FALSE, params = list(show = g_bool)),
  set_y2_axis = list(chartex = FALSE, secondary = TRUE, params = axis_params),
  set_x2_axis = list(chartex = FALSE, secondary = TRUE, params = axis_params),
  set_y2_title = list(chartex = FALSE, secondary = TRUE, params = text_params),
  add_series_combo = list(chartex = FALSE, params = list(
    type = function() sample(c(chart_types, "bogusChart"), 1), secondary = g_choice(list(TRUE, FALSE, "x", "y", "xy", "none"), list("both")),
    grouping = g_choice(list("standard", "stacked", "percentStacked", "clustered"), list("bogus")),
    dir = g_choice(list("col", "bar"), list("up")))),
  add_series_ex_more = list(chartex = TRUE, params = list(
    statistics = g_choice(list("exclusive", "inclusive"), list("median")),
    binning = function() pick(list(list(bin_count = g_number()), list(bin_size = g_number()),
                                   list(interval_closed = pick(list("l", "r", "x"))),
                                   list(underflow = pick(list("auto", g_number()))), list(overflow = g_number()))),
    visibility = function() pick(list(list(mean_line = g_bool()), list(outliers = g_bool()), list(nonoutliers = g_bool()))),
    parent_label = g_choice(list("banner", "overlapping", "none"), list("big")))),
  set_color_cycle = list(chartex = TRUE, params = list(colors = function() pick(list(c("FF0000", "00FF00", "0000FF"),
                                                                                       list(g_color(), g_color(), g_color()),
                                                                                       c("red"), character()))))
)

# cases ----------------------------------------------------------------------

base_workbook <- function() {
  wb_workbook()$
    add_worksheet("Sheet 1")$
    add_data(x = data.frame(a = 1:3, b = c("x", "y", "z")))
}

base_chart_workbook <- function() {
  wb_workbook()$
    add_worksheet("data")$
    add_data(x = data.frame(a = c(3, 5, 2), b = c(2, -1, 4), c = c("p", "q", "r")))
}

draw_args <- function(params) {
  k <- sample.int(min(3, length(params)), 1)
  chosen <- sample(names(params), k)
  vals <- lapply(chosen, function(p) params[[p]]())
  names(vals) <- chosen
  vals
}

make_case_openxlsx2 <- function() {
  fn <- sample(names(targets_openxlsx2), 1)
  t <- targets_openxlsx2[[fn]]
  fuzzed <- draw_args(t$params)
  list(target = fn, type = NA_character_, fuzzed = fuzzed, build = function(f) {
    args <- t$fixed
    args[names(f)] <- f
    c(t$pre, list(as.call(list(as.name("<-"), as.name("wb"),
                               as.call(c(list(as.name(t$fn %||% fn), as.name("wb")), args))))), t$post)
  })
}

make_case_encharter <- function() {
  fn <- sample(names(targets_encharter), 1)
  t <- targets_encharter[[fn]]
  ex <- if (is.na(t$chartex)) runif(1) < 0.3 else t$chartex
  type <- t$type %||% if (ex) sample(chartex_types, 1) else sample(chart_types, 1)
  fuzzed <- draw_args(t$params)
  list(target = paste0(fn, " (", type, ")"), type = type, fuzzed = fuzzed, build = function(f) {
    series <- list(name = "data!$B$1", data = "data!$A$2:$A$4", label = "data!$C$2:$C$4")
    add <- list(dims = "E2:K15", sheet = 1)
    calls <- list(call("<-", as.name("ch"), call("ec", type)))
    if (fn %in% c("add_series", "add_series_ex", "add_series_ex_more")) {
      series[names(f)] <- f
    }
    calls <- c(calls, list(as.call(c(list(call("$", as.name("ch"), as.name("add_series"))), series))))
    if (type == "stockChart") calls <- c(calls, stock_series(), stock_series())
    # a stock chart group needs three series, on either axis
    if (isTRUE(t$secondary)) {
      sec <- as.call(list(call("$", as.name("ch"), as.name("add_series")),
                          name = "data!$B$1", data = "data!$B$2:$B$4", secondary = TRUE))
      calls <- c(calls, rep(list(sec), if (type == "stockChart") 3 else 1))
    }
    if (fn == "add_series_combo") {
      combo <- as.call(c(list(call("$", as.name("ch"), as.name("add_series")),
                              name = "data!$B$1", data = "data!$B$2:$B$4"), f))
      calls <- c(calls, rep(list(combo), if (identical(f$type, "stockChart")) 3 else 1))
    }
    if (grepl("_title$", fn) && is.null(f$text)) f <- c(list(text = "title"), f)
    if (!fn %in% c("add_series", "add_series_ex", "add_series_ex_more", "add_series_combo", "wb_add_encharter")) {
      calls <- c(calls, list(as.call(c(list(call("$", as.name("ch"), as.name(fn))), f))))
    }
    if (fn == "wb_add_encharter") add[names(f)] <- f
    c(calls, list(call("<-", as.name("wb"),
                       as.call(c(list(as.name("wb_add_encharter"), as.name("wb")), add, list(graph = as.name("ch")))))))
  })
}

run_case <- function(calls, file, base) {
  env <- new.env(parent = globalenv())
  env$wb <- base()
  status <- tryCatch({
    withCallingHandlers({
      for (cl in calls) eval(cl, env)
    }, warning = function(w) invokeRestart("muffleWarning"))
    "accepted"
  }, error = function(e) paste("rejected:", conditionMessage(e)))
  if (status != "accepted") return(status)
  tryCatch({
    suppressWarnings(env$wb$save(file))
    "saved"
  }, error = function(e) paste("save failed:", conditionMessage(e)))
}

validate <- function(dir) {
  ign <- unlist(lapply(ignore_files[file.exists(ignore_files)], function(f) c("--ignore-file", shQuote(f))))
  res <- suppressWarnings(system2(validator, c("-f", "tsv", ign, shQuote(dir)), stdout = TRUE, stderr = FALSE))
  if (length(res) == 0) stop("the validator wrote nothing", call. = FALSE)
  df <- read.delim(text = c(res, ""), sep = "\t", quote = "", colClasses = "character", na.strings = "", encoding = "UTF-8")
  for (col in names(df)) Encoding(df[[col]]) <- "UTF-8"
  df$case <- sub("\\.xlsx$", "", basename(df$file))
  df[df$severity == "error", ]
}

ascii <- function(x) iconv(enc2utf8(x), "UTF-8", "ASCII", sub = "Unicode")

short <- function(v) {
  if (is.character(v) && length(v) > 1) {
    return(paste0("c(", paste(vapply(v, short, ""), collapse = ", "), ")"))
  }
  if (is.character(v) && length(v) == 1 && !is.na(v) && nchar(v) > 40) {
    if (length(unique(strsplit(v, "")[[1]])) == 1) return(sprintf("strrep(\"%s\", %d)", substr(v, 1, 1), nchar(v)))
    return(paste0(deparse(substr(v, 1, 30)), "..."))
  }
  paste(deparse(v, width.cutoff = 500L), collapse = "")
}

show_args <- function(f) ascii(paste(names(f), vapply(f, short, ""), sep = " = ", collapse = ", "))

plain_path <- function(x) gsub("\\[[0-9]+\\]", "", x)

# stock charts need high, low and close series
stock_series <- function() {
  list(as.call(list(call("$", as.name("ch"), as.name("add_series")),
                    name = "data!$B$1", data = "data!$B$2:$B$4", label = "data!$C$2:$C$4")))
}

chart_calls <- function(type) {
  list(call("<-", as.name("ch"), call("ec", type)),
       call("$", as.name("ch"), as.name("add_series")),
       call("<-", as.name("wb"), call("wb_add_encharter", as.name("wb"), dims = "E2:K15", graph = as.name("ch"))))
}

sanity_encharter <- function() {
  types <- c(chart_types, chartex_types)
  calls <- lapply(types, function(type) {
    cl <- chart_calls(type)
    cl[[2]] <- as.call(list(cl[[2]], name = "data!$B$1", data = "data!$A$2:$A$4", label = "data!$C$2:$C$4"))
    if (type == "stockChart") cl <- c(cl[1:2], stock_series(), stock_series(), cl[3])
    cl
  })
  names(calls) <- types
  calls
}

fuzz <- function(label, make_case, base, sanity = list(base = list())) {
  dir <- file.path(out_dir, label)
  dir.create(dir, showWarnings = FALSE)
  cases <- vector("list", n_cases)
  status <- character(n_cases)
  for (i in seq_len(n_cases)) {
    cs <- make_case()
    id <- sprintf("%s_%05d", label, i)
    cs$id <- id
    status[i] <- run_case(cs$build(cs$fuzzed), file.path(dir, paste0(id, ".xlsx")), base)
    cases[[i]] <- cs
  }
  names(cases) <- vapply(cases, function(x) x$id, "")

  for (s in names(sanity)) {
    st <- run_case(sanity[[s]], file.path(dir, sprintf("%s_sanity_%s.xlsx", label, s)), base)
    if (st != "saved") cat(sprintf("sanity case %s: %s\n", s, st))
  }

  findings <- validate(dir)
  bad <- sub(paste0("^", label, "_sanity_"), "", unique(findings$case[startsWith(findings$case, paste0(label, "_sanity_"))]))
  if ("base" %in% bad) stop("the base workbook is not valid, fix the fuzzer first", call. = FALSE)
  for (b in bad) {
    r <- findings[findings$case == paste0(label, "_sanity_", b), ][1, ]
    cat(sprintf("\nwithout any fuzzing, a %s chart is already invalid: %s [%s]\n  at %s %s\n",
                b, r$description, r$id, r$part, plain_path(r$xpath)))
  }
  findings <- findings[!startsWith(findings$case, paste0(label, "_sanity_")), ]
  if (length(bad)) {
    findings <- findings[!vapply(cases[findings$case], function(cs) isTRUE(cs$type %in% bad), TRUE), ]
  }

  # attribute multi-argument failures to single arguments
  multi <- unique(findings$case)
  multi <- multi[vapply(cases[multi], function(cs) length(cs$fuzzed) > 1, TRUE)]
  af <- data.frame()
  if (length(multi)) {
    adir <- file.path(dir, "attribution")
    dir.create(adir, showWarnings = FALSE)
    for (id in multi) {
      cs <- cases[[id]]
      for (p in names(cs$fuzzed)) {
        run_case(cs$build(cs$fuzzed[p]), file.path(adir, paste0(id, "__", p, ".xlsx")), base)
      }
    }
    af <- validate(adir)
    if (nrow(af)) {
      af$orig <- sub("__.*$", "", af$case)
      af$param <- sub("^.*__", "", af$case)
      af$key <- paste(af$orig, af$id, plain_path(af$xpath), sep = "\r")
    }
  }

  tgt <- vapply(cases, function(x) sub(" \\(.*\\)$", "", x$target), "")
  never <- setdiff(unique(tgt), unique(tgt[status == "saved"]))
  for (t in never) {
    first <- status[tgt == t][1]
    cat(sprintf("target %s never saved a file (%d cases), e.g. %s\n", t, sum(tgt == t), substr(first, 1, 160)))
  }

  cat(sprintf("\n== %s: %d cases, %d rejected by R, %d save failures, %d saved, %d invalid\n",
              label, n_cases, sum(startsWith(status, "rejected")), sum(startsWith(status, "save failed")),
              sum(status == "saved"), length(unique(findings$case))))

  if (nrow(findings)) {
    findings$target <- vapply(cases[findings$case], function(x) sub(" \\(.*\\)$", "", x$target), "")
    fkey <- paste(findings$case, findings$id, plain_path(findings$xpath), sep = "\r")
    findings$param <- vapply(seq_len(nrow(findings)), function(i) {
      p <- if (nrow(af)) unique(af$param[af$key == fkey[i]]) else character()
      if (!length(p)) p <- names(cases[[findings$case[i]]]$fuzzed)
      paste(p, collapse = "+")
    }, "")
    findings$where <- trimws(paste(sub("[0-9]+\\.xml$", "N.xml", findings$part),
                                   ifelse(is.na(findings$xpath), "", plain_path(findings$xpath))))
    attr <- regmatches(findings$description, regexpr("attribute '[^']+'|'[^']+' attribute", findings$description))
    findings$attr <- ""
    findings$attr[grepl("attribute '[^']+'|'[^']+' attribute", findings$description)] <- attr
    key <- paste(findings$target, findings$id, findings$where, findings$attr, sep = "\r")
    groups <- split(seq_len(nrow(findings)), key)
    groups <- groups[order(-vapply(groups, function(g) length(unique(findings$case[g])), 0))]
    for (g in groups) {
      r <- findings[g[1], ]
      cs <- cases[[r$case]]
      params <- sort(table(unlist(strsplit(findings$param[g], "+", fixed = TRUE))), decreasing = TRUE)
      cat(sprintf("\n%s(%s): %s [%s]\n", r$target, names(params)[1], ascii(r$description), r$id))
      cat(sprintf("  at %s, %d case(s), e.g. %s\n", r$where, length(unique(findings$case[g])), r$case))
      cat(sprintf("  %s: %s\n", cs$target, show_args(cs$fuzzed)))
      writeLines(c("library(openxlsx2)", if (label == "encharter") "library(encharter)",
                   paste0("wb <- (", paste(deparse(body(base)), collapse = "\n"), ")"),
                   vapply(cs$build(cs$fuzzed), function(cl) paste(deparse(cl, width.cutoff = 80L), collapse = "\n"), ""),
                   sprintf("wb$save(\"%s.xlsx\")", r$case)),
                 file.path(dir, paste0(r$case, ".R")))
    }
  }

  crashes <- which(startsWith(status, "save failed"))
  for (i in head(crashes, 10)) {
    cs <- cases[[i]]
    cat(sprintf("\nsave failed after %s: %s\n  %s\n", cs$target, show_args(cs$fuzzed), status[i]))
  }

  if (opt$keep != "TRUE") {
    keep <- unique(findings$case[!duplicated(paste(findings$id, findings$xpath))])
    drop <- list.files(dir, pattern = "\\.xlsx$", full.names = TRUE)
    drop <- drop[!sub("\\.xlsx$", "", basename(drop)) %in% keep]
    unlink(drop)
    unlink(file.path(dir, "attribution"), recursive = TRUE)
  }
  invisible(findings)
}

cat("validator:", validator, "\nresults:  ", out_dir, "\n")
if (opt$target %in% c("all", "openxlsx2")) fuzz("openxlsx2", make_case_openxlsx2, base_workbook)
if (opt$target %in% c("all", "encharter")) {
  suppressPackageStartupMessages(library(encharter))
  fuzz("encharter", make_case_encharter, base_chart_workbook, sanity_encharter())
}
