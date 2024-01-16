
//
// Copyright (C) 2020 David Carpenter
// 
// Permission is hereby granted, free of charge, 
// to any person obtaining a copy of this software 
// and associated documentation files (the "Software"), 
// to deal in the Software without restriction, 
// including without limitation the rights to use, 
// copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit 
// persons to whom the Software is furnished to do so, 
// subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be 
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, 
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES 
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. 
// IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, 
// DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, 
// TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
// SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

// Everything command line interface via IPC.

// TODO:
// Use system number formatting (some localizations don't use , for the comma)
// Show the sum of the size.
// add an option to append a path separator for folders.
// add a -no-config command line option to bypass loading of settings.
// would be nice if it had -POSIX and -NUL switches to generate NUL for RG/GREP
// export as TSV to match Everything export.
// -save-db -save database to disk command line option.
// -rebuild -force a rebuild
// inc-run-count/set-run-count should allow -instance
// fix background highlight when a result spans multiple lines, eg: -highlight -highlight-color 0xa0
// es sonic -name -path-column -name-color 13 -highlight // -name-color 13 doesn't work with -highlight !
// improve -pause when the window is resized.
// -v to display everything version?
// -path is not working with -r It is using the <"filter"> (-path is adding brackets and double quotes, neither of which regex understands)
// Gather file information if it is not indexed.
// path ellipsis?
// -get-size <filename> - expand -get-run-count to get other information for a single file? -what happens when the file doesn't exist?
// when in pause:
//		w - wrap
//		n - number
//		/ - search
//		h - help
//		r - write
//		default "prompt", ":".
//		If you want help, :h.
//		Save the stream to a file, :r c:\tmp\out.txt
// Maybe a configurable prompt, so the "current" (bottom-most) line, total count of results. 
// [23/72]:
// emulate LESS
// *shouldn't we use environment variables for storing switches? like MORE? -registry does make it easy. -using ini now
// custom date/time format.

// NOTES:
// ScrollConsoleScreenBuffer is unuseable because it fills invalidated areas, which causes unnecessary flickering.

//#define ES_VERSION L"1.0.0.0" // initial release
//#define ES_VERSION L"1.0.0.1" // fixed command line interpreting '-' as a switch inside text.
//#define ES_VERSION L"1.0.0.2" // convert unicode to same code page as console.
//#define ES_VERSION L"1.0.0.3" // removed es_write console because it has no support for piping.
//#define ES_VERSION L"1.0.0.4" // added ChangeWindowMessageFilterEx (if available) for admin/user support.
//#define ES_VERSION L"1.1.0.1a" // Everything 1.4.1 support for size, dates etc.
//#define ES_VERSION L"1.1.0.2a" // -pause, -more, -hide-empty-search-results, -empty-search-help, -save-settings, -clear-settings, -path, -parent-path, -parent -no-*switches*, localized dates
//#define ES_VERSION L"1.1.0.3a" // error levels added, -timeout
//#define ES_VERSION L"1.1.0.4a" // -pause improvements, -set-run-count, -inc-run-count, -get-run-count, allow -name -size -date to re-order columns even when they are specified in settings, with -more ENTER=show one line, Q=Quit, ESC=Escape., column alignment improvements.
//#define ES_VERSION L"1.1.0.5a" // /a[RASH] attributes added, -more/-pause ignored for no results, -sort-size, -sort-size-ascending and -sort-size-descending.
//#define ES_VERSION L"1.1.0.6a" // added -csv, -efu, -txt, -m3u and -m3u8 to set the display format (no exporting), added -export-m3u and -export-m3u8.
//#define ES_VERSION L"1.1.0.7" // Everything 1.4 release.
//#define ES_VERSION L"1.1.0.8" // removed -highligh-name, -highligh-path and -highligh-full-path-name. fixed date widths, switched to ini settings.
//#define ES_VERSION L"1.1.0.9" // fixed an issue with checking if the database is loaded, exporting as efu only exports indexed information now, use -size, -date-modified, -date-created or -attributes to override., folder size is also exported now.
//#define ES_VERSION L"1.1.0.10" // -r now takes a parameter, and Everything handles escaping quotes.
//#define ES_VERSION L"1.1.0.11" // added -date-format
//#define ES_VERSION L"1.1.0.12" // added -get-result-count
//#define ES_VERSION L"1.1.0.13" // added -cd -removed -cd -added -ipc1 -ipc2 -output errors to std_error
//#define ES_VERSION L"1.1.0.14" // updated help (thanks to NotNull) and fixed -? -h -help errors
//#define ES_VERSION L"1.1.0.15" // updated help (thanks to NotNull) 
//#define ES_VERSION L"1.1.0.16" // added -no-header, added -double-quote, added -version, added -get-everything-version
//#define ES_VERSION L"1.1.0.17" // added -no-result-error
//#define ES_VERSION L"1.1.0.18" // added -get-total-size
//#define ES_VERSION L"1.1.0.19" // inc-run-count/set-run-count allow -instance
//#define ES_VERSION L"1.1.0.20" // fix ALT in pause mode causing page down.
//#define ES_VERSION L"1.1.0.21" // added TSV support.
//#define ES_VERSION L"1.1.0.22" // fixed an issue with WideCharToMultiByte using the wrong buffer size.
//#define ES_VERSION L"1.1.0.23" // fixed an issue with instance name not being initialized.
//#define ES_VERSION L"1.1.0.24" // fixed an issue with converting ini utf-8 to wchar text.
//#define ES_VERSION L"1.1.0.25" // added -exit and -reindex -utf8-bom
//#define ES_VERSION L"1.1.0.26" // fixed an issue with exporting UTF-16 surrogate pairs.


// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***
// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***
// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***
// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***
// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***
// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***
// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***
#define ES_VERSION L"1.1.0.26" 
// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***
// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***
// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***
// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***
// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***
// *** YOU MUST MANUALLY UPDATE THE RESOURCE FILE WITH THE NEW VERSION ***

// errorlevel returned from ES.
#define ES_ERROR_SUCCESS					0 // no known error, search successful.
#define ES_ERROR_REGISTER_WINDOW_CLASS		1 // failed to register window class
#define ES_ERROR_CREATE_WINDOW				2 // failed to create listening window.
#define ES_ERROR_OUT_OF_MEMORY				3 // out of memory
#define ES_ERROR_EXPECTED_SWITCH_PARAMETER	4 // expected an additional command line option with the specified switch
#define ES_ERROR_CREATE_FILE				5 // failed to create export output file
#define ES_ERROR_UNKNOWN_SWITCH				6 // unknown switch.
#define ES_ERROR_SEND_MESSAGE				7 // failed to send Everything IPC a query.
#define ES_ERROR_IPC						8 // NO Everything IPC window.
#define ES_ERROR_NO_RESULTS					9 // No results found. Only set if -no-result-error is used

// compiler options
#pragma warning(disable : 4996) // deprecation

#include <windows.h>
#include <stdio.h>

// IPC Header from the Everything SDK:
#include "..\..\sdk\ipc\everything_ipc.h"
#include "shlwapi.h" // Path functions

#define ES_COPYDATA_IPCTEST_QUERYCOMPLETEW		0
#define ES_COPYDATA_IPCTEST_QUERYCOMPLETE2W		1

#define ES_EXPORT_NONE		0
#define ES_EXPORT_CSV		1
#define ES_EXPORT_EFU		2
#define ES_EXPORT_TXT		3
#define ES_EXPORT_M3U		4
#define ES_EXPORT_M3U8		5
#define ES_EXPORT_TSV		6

#define ES_BUF_SIZE			(MAX_PATH)
#define ES_EXPORT_BUF_SIZE	65536
#define ES_SEARCH_BUF_SIZE	(1 * 1024 * 1024)
#define ES_FILTER_BUF_SIZE	(1 * 1024 * 1024)

#define ES_PAUSE_TEXT		"ESC=Quit; Up,Down,Left,Right,Page Up,Page Down,Home,End=Scroll"
#define ES_BLANK_PAUSE_TEXT	"                                                              "


enum
{
	ES_COLUMN_FILENAME,
	ES_COLUMN_NAME,
	ES_COLUMN_PATH,
	ES_COLUMN_HIGHLIGHTED_FILENAME,
	ES_COLUMN_HIGHLIGHTED_NAME,
	ES_COLUMN_HIGHLIGHTED_PATH,
	ES_COLUMN_EXTENSION,
	ES_COLUMN_SIZE,
	ES_COLUMN_DATE_CREATED,
	ES_COLUMN_DATE_MODIFIED,
	ES_COLUMN_DATE_ACCESSED,
	ES_COLUMN_ATTRIBUTES,
	ES_COLUMN_FILE_LIST_FILENAME,
	ES_COLUMN_RUN_COUNT,
	ES_COLUMN_DATE_RUN,
	ES_COLUMN_DATE_RECENTLY_CHANGED,
	ES_COLUMN_TOTAL,
};

const wchar_t *es_column_names[] = 
{
	L"Filename",
	L"Name",
	L"Path",
	L"Filename", // highlighted
	L"Name", // highlighted
	L"Path", // highlighted
	L"Extension",
	L"Size",
	L"Date Created",
	L"Date Modified",
	L"Date Accessed",
	L"Attributes",
	L"File List Filename",
	L"Run Count",
	L"Date Run",
	L"Date Recently Changed",
};

const wchar_t *es_sort_names[] = 
{
	L"name",
	L"path",
	L"size",
	L"extension",
	L"ext",
	L"date-created",
	L"dc",
	L"date-modified",
	L"dm",
	L"attributes",
	L"attribs",
	L"attrib",
	L"file-list-file-name",
	L"run-count",
	L"recent-change",
	L"date-recently-changed",
	L"rc",
	L"drc",
	L"date-accessed",
	L"da",
	L"date-run",
	L"daterun",
};

#define ES_SORT_NAME_COUNT (sizeof(es_sort_names) / sizeof(const wchar_t *))

const DWORD es_sort_names_to_ids[] = 
{
	EVERYTHING_IPC_SORT_NAME_ASCENDING,
	EVERYTHING_IPC_SORT_PATH_ASCENDING,
	EVERYTHING_IPC_SORT_SIZE_DESCENDING,
	EVERYTHING_IPC_SORT_EXTENSION_ASCENDING,
	EVERYTHING_IPC_SORT_EXTENSION_ASCENDING,
	EVERYTHING_IPC_SORT_DATE_CREATED_DESCENDING,
	EVERYTHING_IPC_SORT_DATE_CREATED_DESCENDING,
	EVERYTHING_IPC_SORT_DATE_MODIFIED_DESCENDING,
	EVERYTHING_IPC_SORT_DATE_MODIFIED_DESCENDING,
	EVERYTHING_IPC_SORT_ATTRIBUTES_ASCENDING,
	EVERYTHING_IPC_SORT_ATTRIBUTES_ASCENDING,
	EVERYTHING_IPC_SORT_ATTRIBUTES_ASCENDING,
	EVERYTHING_IPC_SORT_FILE_LIST_FILENAME_ASCENDING,
	EVERYTHING_IPC_SORT_RUN_COUNT_DESCENDING,
	EVERYTHING_IPC_SORT_DATE_RECENTLY_CHANGED_DESCENDING,
	EVERYTHING_IPC_SORT_DATE_RECENTLY_CHANGED_DESCENDING,
	EVERYTHING_IPC_SORT_DATE_RECENTLY_CHANGED_DESCENDING,
	EVERYTHING_IPC_SORT_DATE_RECENTLY_CHANGED_DESCENDING,
	EVERYTHING_IPC_SORT_DATE_ACCESSED_DESCENDING,
	EVERYTHING_IPC_SORT_DATE_ACCESSED_DESCENDING,
	EVERYTHING_IPC_SORT_DATE_RUN_DESCENDING,
	EVERYTHING_IPC_SORT_DATE_RUN_DESCENDING,
};

#define MSGFLT_ALLOW		1

typedef struct tagCHANGEFILTERSTRUCT 
{
	DWORD cbSize;
	DWORD ExtStatus;
}CHANGEFILTERSTRUCT, *PCHANGEFILTERSTRUCT;

typedef unsigned __int64 QWORD;

void DECLSPEC_NORETURN es_fatal(int error_code);
void es_write(const wchar_t *text);
void es_fwrite(const wchar_t *text);
void es_fwrite_n(const wchar_t *text,int wlen);
void es_write_highlighted(const wchar_t *text);
void es_write_QWORD(QWORD value);
void es_write_DWORD(DWORD value);
int es_wstring_to_int(const wchar_t *s);
int es_wstring_length(const wchar_t *s);
void es_format_number(wchar_t *buf,QWORD number);
int es_sendquery(HWND hwnd);
int es_sendquery2(HWND hwnd);
int es_compare_list_items(const void *a,const void *b);
void es_write_result_count(DWORD result_count);
void es_listresultsW(EVERYTHING_IPC_LIST *list);
void es_listresults2W(EVERYTHING_IPC_LIST2 *list,int index_start,int count);
void es_write_total_size(EVERYTHING_IPC_LIST2 *list,int count);
LRESULT __stdcall es_window_proc(HWND hwnd,UINT msg,WPARAM wParam,LPARAM lParam);
void es_help(void);
HWND es_find_ipc_window(void);
void es_wstring_copy(wchar_t *buf,const wchar_t *s);
void es_wstring_cat(wchar_t *buf,const wchar_t *s);
int es_check_param(wchar_t *param,const wchar_t *s);
int es_wstring_compare_param(wchar_t *param,const wchar_t *s);
void es_set_sort_ascending(void);
void es_set_sort_descending(void);
int es_find_column(int type);
void es_add_column(int type);
void *es_get_column_data(EVERYTHING_IPC_LIST2 *list,int index,int type);
void es_format_dir(wchar_t *buf);
void es_format_size(wchar_t *buf,QWORD size);
void es_format_filetime(wchar_t *buf,QWORD filetime);
void es_wstring_print_qword(wchar_t *buf,QWORD number);
void es_format_attributes(wchar_t *buf,DWORD attributes);
int es_filetime_to_localtime(SYSTEMTIME *localst,QWORD ft);
void es_format_leading_space(wchar_t *buf,int size,int ch);
void es_space_to_width(wchar_t *buf,int wide);
void es_format_run_count(wchar_t *buf,DWORD run_count);
int es_check_param_param(wchar_t *param,const wchar_t *s);
void es_flush(void);
void *es_alloc(uintptr_t size);
void es_free(void *ptr);
void es_fwrite_csv_string(const wchar_t *s);
const wchar_t *es_get_command_argv(const wchar_t *command_line);
const wchar_t *es_get_argv(const wchar_t *command_line);
const wchar_t *es_skip_ws(const wchar_t *p);
int es_is_ws(const wchar_t c);
void es_set_color(int id);
void es_set_column_wide(int id);
void es_fill(int count,int utf8ch);
void es_wstring_cat_qword(wchar_t *buf,QWORD qw);
int es_is_valid_key(INPUT_RECORD *ir);
void es_ini_write_int(const char *name,int value,const char *filename);
void es_ini_write_string(const char *name,const wchar_t *value,const char *filename);
void es_save_settings(void);
int es_ini_read_int(const char *name,int *pvalue,const char *filename);
int es_ini_read_string(const char *name,wchar_t *pvalue,const char *filename);
void es_load_settings(void);
void es_remove_column(type);
void es_append_filter(const wchar_t *filter);
void es_wbuf_cat(wchar_t *buf,int max,const wchar_t *s);
wchar_t *es_wstring_alloc(const wchar_t *s);
void es_do_run_history_command(void);
int es_check_sorts(void);
int es_get_ini_filename(char *buf);
static void es_wait_for_db_loaded(void);
static void es_wait_for_db_not_busy(void);

int es_sort = EVERYTHING_IPC_SORT_NAME_ASCENDING;
int es_sort_ascending = 0; // 0 = default, >0 = ascending, <0 = descending
EVERYTHING_IPC_LIST *es_sort_list;
HMODULE es_user32_hdll = 0;
wchar_t *es_instance = 0;
BOOL (WINAPI *es_pChangeWindowMessageFilterEx)(HWND hWnd,UINT message,DWORD action,PCHANGEFILTERSTRUCT pChangeFilterStruct) = 0;
int es_highlight_color = FOREGROUND_GREEN|FOREGROUND_INTENSITY;
int es_highlight = 0;
int es_match_whole_word = 0;
int es_match_path = 0;
int es_match_case = 0;
int es_match_diacritics = 0;
int es_exit_everything = 0;
int es_reindex = 0;
int es_save_db = 0;
int es_export = ES_EXPORT_NONE;
HANDLE es_export_file = INVALID_HANDLE_VALUE;
unsigned char *es_export_buf = 0;
unsigned char *es_export_p;
int es_export_remaining = 0;
int es_numcolumns = 0;
int es_columns[ES_COLUMN_TOTAL];
int es_size_leading_zero = 0;
int es_run_count_leading_zero = 0;
int es_digit_grouping = 1;
DWORD es_offset = 0;
DWORD es_max_results = EVERYTHING_IPC_ALLRESULTS;
DWORD es_ret = ES_ERROR_SUCCESS;
wchar_t *es_connect = 0;
wchar_t *es_search = 0;
wchar_t *es_filter = 0;
wchar_t *es_argv = 0;
WORD es_column_color[ES_COLUMN_TOTAL];
WORD es_column_color_is_valid[ES_COLUMN_TOTAL] = {0};
WORD es_last_color;
int es_is_last_color = 0;
int es_size_format = 1; // 0 = auto, 1=bytes, 2=kb
int es_date_format = 0; // 0 = system format, 1=iso-8601 (as local time), 2=filetime in decimal, 3=iso-8601 (in utc)
CHAR_INFO *es_cibuf = 0;
int es_max_wide = 0;
int es_console_wide = 80;
int es_console_high = 25;
int es_console_size_high = 25;
int es_console_window_x = 0;
int es_console_window_y = 0;
int es_pause = 0; 
int es_last_page_start = 0; 
int es_cibuf_attributes = 0x07;
int es_cibuf_hscroll = 0;
int es_cibuf_x = 0;
int es_cibuf_y = 0;
int es_empty_search_help = 0;
int es_hide_empty_search_results = 0;
int es_save = 0;
HWND es_everything_hwnd = 0;
DWORD es_timeout = 0;
int es_get_result_count = 0; // 1 = show result count only
int es_get_total_size = 0; // 1 = calculate total result size, only display this total size and exit.
int es_no_result_error = 0; // 1 = set errorlevel if no results found.
HANDLE es_output_handle = 0;
int es_output_is_file = 1; // default to file, unless we can get the console mode.
int es_default_attributes = 0x07;
void *es_run_history_data = 0; // run count command
DWORD es_run_history_count = 0; // run count command
int es_run_history_command = 0;
int es_run_history_size = 0;
DWORD es_ipc_version = 0xffffffff;
int es_header = 1;
int es_double_quote = 0; // always use double quotes for filenames.
int es_get_everything_version = 0;
int es_utf8_bom = 0;

int es_column_widths[ES_COLUMN_TOTAL] = 
{
	47, // ES_COLUMN_FILENAME,
	30, // ES_COLUMN_NAME,
	47, // ES_COLUMN_PATH,
	47, // ES_COLUMN_HIGHLIGHTED_FILENAME,
	30, // ES_COLUMN_HIGHLIGHTED_NAME,
	47, // ES_COLUMN_HIGHLIGHTED_PATH,
	4, // ES_COLUMN_EXTENSION,
	14, // ES_COLUMN_SIZE,
	16, // ES_COLUMN_DATE_CREATED,
	16, // ES_COLUMN_DATE_MODIFIED,
	16, // ES_COLUMN_DATE_ACCESSED,
	4, // ES_COLUMN_ATTRIBUTES,
	30, // ES_COLUMN_FILE_LIST_FILENAME,
	6, // ES_COLUMN_RUN_COUNT,
	16, // ES_COLUMN_DATE_RUN,
	16, // ES_COLUMN_DATE_RECENTLY_CHANGED,
};

int es_column_is_right_aligned[ES_COLUMN_TOTAL] = 
{
	0, // ES_COLUMN_FILENAME,
	0, // ES_COLUMN_NAME,
	0, // ES_COLUMN_PATH,
	0, // ES_COLUMN_HIGHLIGHTED_FILENAME,
	0, // ES_COLUMN_HIGHLIGHTED_NAME,
	0, // ES_COLUMN_HIGHLIGHTED_PATH,
	0, // ES_COLUMN_EXTENSION,
	1, // ES_COLUMN_SIZE,
	0, // ES_COLUMN_DATE_CREATED,
	0, // ES_COLUMN_DATE_MODIFIED,
	0, // ES_COLUMN_DATE_ACCESSED,
	0, // ES_COLUMN_ATTRIBUTES,
	0, // ES_COLUMN_FILE_LIST_FILENAME,
	1, // ES_COLUMN_RUN_COUNT,
	0, // ES_COLUMN_DATE_RUN,
	0, // ES_COLUMN_DATE_RECENTLY_CHANGED,
};

// get the length in wchars from a wchar string.
int es_wstring_length(const wchar_t *s)
{
	const wchar_t *p;
	
	p = s;
	while(*p)
	{
		p++;
	}
	
	return (int)(p - s);
}

// get the length in wchars from highlighted string
// skips over the '*' parts
int es_wstring_highlighted_length(const wchar_t *s)
{
	const wchar_t *p;
	int len;
	
	len = 0;
	p = s;
	while(*p)
	{
		if (*p == '*')
		{
			p++;
			
			if (*p == '*')
			{
				len++;
				p++;
			}
		}
		else
		{
			p++;
			len++;
		}
	}
	
	return len;
}

// query everything with search string
int es_sendquery(HWND hwnd)
{
	EVERYTHING_IPC_QUERY *query;
	int len;
	int size;
	COPYDATASTRUCT cds;
	int ret;
	
	ret = 0;
	
	len = es_wstring_length(es_search);
	
	size = sizeof(EVERYTHING_IPC_QUERY) - sizeof(WCHAR) + len*sizeof(WCHAR) + sizeof(WCHAR);
	
	query = (EVERYTHING_IPC_QUERY *)es_alloc(size);

	if (es_get_result_count)
	{
		es_max_results = 0;
	}
	
	query->max_results = es_max_results;
	query->offset = 0;
	query->reply_copydata_message = ES_COPYDATA_IPCTEST_QUERYCOMPLETEW;
	query->search_flags = (es_match_case?EVERYTHING_IPC_MATCHCASE:0) | (es_match_diacritics?EVERYTHING_IPC_MATCHACCENTS:0) | (es_match_whole_word?EVERYTHING_IPC_MATCHWHOLEWORD:0) | (es_match_path?EVERYTHING_IPC_MATCHPATH:0);
	query->reply_hwnd = (DWORD)(uintptr_t)hwnd;
	CopyMemory(query->search_string,es_search,(len+1)*sizeof(WCHAR));

	cds.cbData = size;
	cds.dwData = EVERYTHING_IPC_COPYDATAQUERY;
	cds.lpData = query;

	if (SendMessage(es_everything_hwnd,WM_COPYDATA,(WPARAM)hwnd,(LPARAM)&cds) == TRUE)
	{
		ret = 1;
	}

	es_free(query);

	return ret;
}

// query everything with search string
int es_sendquery2(HWND hwnd)
{
	EVERYTHING_IPC_QUERY2 *query;
	int len;
	int size;
	COPYDATASTRUCT cds;
	int ret;
	DWORD request_flags;
	
	ret = 0;

	len = es_wstring_length(es_search);
	
	size = sizeof(EVERYTHING_IPC_QUERY2) + ((len + 1) * sizeof(WCHAR));
	
	query = (EVERYTHING_IPC_QUERY2 *)es_alloc(size);

	request_flags = 0;
	
	{
		int columni;
		
		for(columni=0;columni<es_numcolumns;columni++)
		{
			switch(es_columns[columni])
			{
				case ES_COLUMN_FILENAME:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_FULL_PATH_AND_NAME;
					break;
					
				case ES_COLUMN_NAME:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_NAME;
					break;
					
				case ES_COLUMN_PATH:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_PATH;
					break;
					
				case ES_COLUMN_HIGHLIGHTED_FILENAME:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_HIGHLIGHTED_FULL_PATH_AND_NAME;
					break;
					
				case ES_COLUMN_HIGHLIGHTED_NAME:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_HIGHLIGHTED_NAME;
					break;
					
				case ES_COLUMN_HIGHLIGHTED_PATH:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_HIGHLIGHTED_PATH;
					break;
					
				case ES_COLUMN_EXTENSION:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_EXTENSION;
					break;

				case ES_COLUMN_SIZE:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_SIZE;
					break;

				case ES_COLUMN_DATE_CREATED:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_DATE_CREATED;
					break;

				case ES_COLUMN_DATE_MODIFIED:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_DATE_MODIFIED;
					break;

				case ES_COLUMN_DATE_ACCESSED:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_DATE_ACCESSED;
					break;

				case ES_COLUMN_ATTRIBUTES:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_ATTRIBUTES;
					break;

				case ES_COLUMN_FILE_LIST_FILENAME:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_FILE_LIST_FILE_NAME;
					break;

				case ES_COLUMN_RUN_COUNT:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_RUN_COUNT;
					break;

				case ES_COLUMN_DATE_RUN:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_DATE_RUN;
					break;

				case ES_COLUMN_DATE_RECENTLY_CHANGED:
					request_flags |= EVERYTHING_IPC_QUERY2_REQUEST_DATE_RECENTLY_CHANGED;
					break;
			}
		}
	}
	
	if (es_get_result_count)
	{
		es_max_results = 0;
		request_flags = 0;
	}
	
	if (es_get_total_size)
	{
		// we only want size.
		request_flags = EVERYTHING_IPC_QUERY2_REQUEST_SIZE;
		es_max_results = EVERYTHING_IPC_ALLRESULTS;
	}
	
	query->max_results = es_max_results;
	query->offset = es_offset;
	query->reply_copydata_message = ES_COPYDATA_IPCTEST_QUERYCOMPLETE2W;
	query->search_flags = (es_match_case?EVERYTHING_IPC_MATCHCASE:0) | (es_match_diacritics?EVERYTHING_IPC_MATCHACCENTS:0) | (es_match_whole_word?EVERYTHING_IPC_MATCHWHOLEWORD:0) | (es_match_path?EVERYTHING_IPC_MATCHPATH:0);
	query->reply_hwnd = (DWORD)(uintptr_t)hwnd;
	query->request_flags = request_flags;
	query->sort_type = es_sort;
	CopyMemory(query+1,es_search,(len + 1) * sizeof(WCHAR));

	cds.cbData = size;
	cds.dwData = EVERYTHING_IPC_COPYDATA_QUERY2;
	cds.lpData = query;

	if (SendMessage(es_everything_hwnd,WM_COPYDATA,(WPARAM)hwnd,(LPARAM)&cds) == TRUE)
	{
		ret = 1;
	}

	es_free(query);

	return ret;
}

int es_compare_list_items(const void *a,const void *b)
{
	int i;
	
	i = wcsicmp(EVERYTHING_IPC_ITEMPATH(es_sort_list,a),EVERYTHING_IPC_ITEMPATH(es_sort_list,b));
	
	if (!i)
	{
		return wcsicmp(EVERYTHING_IPC_ITEMFILENAME(es_sort_list,a),EVERYTHING_IPC_ITEMFILENAME(es_sort_list,b));
	}
	else
	if (i > 0)
	{
		return 1;
	}
	else
	{
		return -1;
	}
}

void DECLSPEC_NORETURN es_fatal(int error_code)
{
	const char *msg;
	int show_help;
	
	msg = 0;
	show_help = 0;

	switch(error_code)
	{
		case ES_ERROR_REGISTER_WINDOW_CLASS:
			msg = "Error 1: Failed to register window class.\r\n";
			break;
			
		case ES_ERROR_CREATE_WINDOW:
			msg = "Error 2: Failed to create window.\r\n";
			break;
			
		case ES_ERROR_OUT_OF_MEMORY:
			msg = "Error 3: Out of memory.\r\n";
			break;
			
		case ES_ERROR_EXPECTED_SWITCH_PARAMETER:
			msg = "Error 4: Expected switch parameter.\r\n";
			// this error is permanent, show help:
			show_help = 1;
			break;
			
		case ES_ERROR_CREATE_FILE:
			msg = "Error 5: Failed to create output file.\r\n";
			break;
			
		case ES_ERROR_UNKNOWN_SWITCH:
			msg = "Error 6: Unknown switch.\r\n";
			// this error is permanent, show help:
			show_help = 1;
			break;
			
		case ES_ERROR_SEND_MESSAGE:
			msg = "Error 7: Unable to send IPC message.\r\n";
			break;
			
		case ES_ERROR_IPC:
			msg = "Error 8: Everything IPC window not found. Please make sure Everything is running.\r\n";
			break;
			
		case ES_ERROR_NO_RESULTS:
			msg = "Error 9: No results found.\r\n";
			break;
	}
	
	if (msg)
	{
		DWORD numwritten;
		
		// msg is ASCII only.
		WriteFile(GetStdHandle(STD_ERROR_HANDLE),msg,(int)strlen(msg),&numwritten,0);
	}
	
	if (show_help)
	{
		es_help();
	}

	ExitProcess(error_code);
}

void es_write(const wchar_t *text)
{
	int wlen;
	
	wlen = es_wstring_length(text);
	
	if (es_cibuf)
	{
		int i;
		
		for(i=0;i<wlen;i++)
		{
			if (i + es_cibuf_x >= es_console_wide)
			{
				break;
			}

			if (i + es_cibuf_x >= 0)
			{
				es_cibuf[i+es_cibuf_x].Attributes = es_cibuf_attributes;
				es_cibuf[i+es_cibuf_x].Char.UnicodeChar = text[i];
			}
		}
		
		es_cibuf_x += wlen;
	}
	else
	{
		// pipe?
		if (es_output_is_file)
		{
			int len;
			
			len = WideCharToMultiByte(GetConsoleCP(),0,text,wlen,0,0,0,0);
			if (len)
			{
				DWORD numwritten;
				char *buf;
				
				buf = es_alloc(len);

				WideCharToMultiByte(GetConsoleCP(),0,text,wlen,buf,len,0,0);
				
				WriteFile(es_output_handle,buf,len,&numwritten,0);
				
				es_free(buf);
			}
		}
		else
		{
			DWORD numwritten;
			
			WriteConsoleW(es_output_handle,text,wlen,&numwritten,0);
		}
	}
}

void es_fill(int count,int utf8ch)
{
	if (es_cibuf)
	{
		int i;
		
		for(i=0;i<count;i++)
		{
			if (i + es_cibuf_x >= es_console_wide)
			{
				break;
			}

			if (i + es_cibuf_x >= 0)
			{
				es_cibuf[i+es_cibuf_x].Attributes = es_cibuf_attributes;
				es_cibuf[i+es_cibuf_x].Char.UnicodeChar = utf8ch;
			}
		}
		
		es_cibuf_x += count;
	}
	else
	{
		unsigned char *buf;
		unsigned char *p;
		DWORD numwritten;
		int i;
		
		buf = es_alloc(count);

		p = buf;
		
		for(i=0;i<count;i++)
		{
			*p++ = utf8ch;
		}

 		WriteFile(es_output_handle,buf,count,&numwritten,0);
		
		es_free(buf);
	}
}

// set wlen to -1 for null terminated text.
void es_fwrite_n(const wchar_t *text,int wlen)
{
	if (wlen)
	{
		if (es_export_file != INVALID_HANDLE_VALUE)
		{
			int len;
			unsigned char stackbuf[ES_BUF_SIZE];
			unsigned char *buf;
			DWORD bufsize;
			DWORD numwritten;
			int cp;
			
			cp = CP_UTF8;

			if (es_export == ES_EXPORT_M3U)		
			{
				cp = CP_ACP;
			}
			
			len = WideCharToMultiByte(cp,0,text,wlen,0,0,0,0);
			
			if (len < ES_BUF_SIZE)
			{
				buf = stackbuf;
				bufsize = ES_BUF_SIZE;
			}
			else
			{
				buf = es_alloc(len);
				bufsize = len;
			}

			len = WideCharToMultiByte(cp,0,text,wlen,buf,bufsize,0,0);
			
			if (wlen < 0)
			{
				// remove null from len.
				if (len) 
				{
					len--;
				}
			}
			
			if (len > es_export_remaining)
			{
				es_flush();
				
				if (len >= ES_EXPORT_BUF_SIZE)
				{
					WriteFile(es_export_file,buf,len,&numwritten,0);
					
					goto copied;
				}
			}

			es_export_remaining -= len;
			CopyMemory(es_export_p,buf,len);
			es_export_p += len;

	copied:
					
			if (buf != stackbuf)
			{
				es_free(buf);
			}
		}
		else
		{
			es_write(text);
		}
	}
}

void es_fwrite(const wchar_t *text)
{
	es_fwrite_n(text,-1);
}

void es_fwrite_csv_string(const wchar_t *s)
{
	const wchar_t *start;
	const wchar_t *p;
	
	es_fwrite(L"\"");
	
	// escape double quotes with double double quotes.
	start = s;
	p = s;
	
	while(*p)
	{
		if (*p == '"')
		{
			es_fwrite_n(start,(int)(p-start));

			// escape double quotes with double double quotes.
			es_fwrite(L"\"");
			es_fwrite(L"\"");
			
			start = p + 1;
		}
		
		p++;
	}

	es_fwrite_n(start,(int)(p-start));
	
	es_fwrite(L"\"");
}

void es_flush(void)
{
	if (es_export_file != INVALID_HANDLE_VALUE)
	{
		if (es_export_remaining != ES_EXPORT_BUF_SIZE)
		{
			DWORD numwritten;
			
			WriteFile(es_export_file,es_export_buf,ES_EXPORT_BUF_SIZE - es_export_remaining,&numwritten,0);
			
			es_export_p = es_export_buf;
			es_export_remaining = ES_EXPORT_BUF_SIZE;
		}	
	}
}

void es_write_highlighted(const wchar_t *text)
{
	if (es_cibuf)
	{
		const wchar_t *p;
		int highlighted;
		
		highlighted = 0;
		
		p = text;
		while(*p)
		{
			int i;
			const wchar_t *start;
			int toggle_highlight;
			int wlen;
			
			start = p;
			toggle_highlight = 0;
			
			for(;;)
			{
				if (!*p)
				{
					wlen = (int)(p-start);

					break;
				}
				
				if (*p == '*')
				{
					if (p[1] == '*')
					{
						// include one literal *
						wlen = (int)(p+1-start);
						p+=2;
						break;
					}
					
					wlen = (int)(p-start);
					p++;
					toggle_highlight = 1;
					break;
				}
				
				p++;
			}
		
			for(i=0;i<wlen;i++)
			{
				if (i + es_cibuf_x >= es_console_wide)
				{
					break;
				}

				if (i + es_cibuf_x >= 0)
				{
					es_cibuf[i+es_cibuf_x].Attributes = es_cibuf_attributes;
					es_cibuf[i+es_cibuf_x].Char.UnicodeChar = start[i];
				}
			}
			
			es_cibuf_x += wlen;

			// skip *				
			if (toggle_highlight)
			{
				highlighted = !highlighted;

				if (highlighted)
				{
					es_cibuf_attributes = es_highlight_color;
				}
				else
				{
					if (es_is_last_color)
					{
						es_cibuf_attributes = es_last_color;
					}
					else
					{
						es_cibuf_attributes = es_default_attributes;
					}
				}				
			}			
		}
	}
	else
	{
		const wchar_t *p;
		int highlighted;
		int attributes;
		
		if (es_is_last_color)
		{
			attributes = es_last_color;
		}
		else
		{
			attributes = es_default_attributes;
		}
		
		highlighted = 0;
		
		p = text;
		while(*p)
		{
			const wchar_t *start;
			int toggle_highlight;
			int wlen;
			
			start = p;
			toggle_highlight = 0;
			
			for(;;)
			{
				if (!*p)
				{
					wlen = (int)(p-start);

					break;
				}
				
				if (*p == '*')
				{
					if (p[1] == '*')
					{
						// include one literal *
						wlen = (int)(p+1-start);
						p+=2;
						break;
					}
					
					wlen = (int)(p-start);
					p++;
					toggle_highlight = 1;
					break;
				}
				
				p++;
			}
		
			if (es_output_is_file)
			{
				int mblen;
				
				mblen = WideCharToMultiByte(GetConsoleCP(),0,start,wlen,0,0,0,0);
				if (mblen)
				{
					DWORD numwritten;
					char *buf;
					
					buf = es_alloc(mblen);

					WideCharToMultiByte(GetConsoleCP(),0,start,wlen,buf,mblen,0,0);
					
					WriteFile(es_output_handle,buf,mblen,&numwritten,0);
					
					es_free(buf);
				}			
			}
			else
			{
				DWORD numwritten;
				
				WriteConsoleW(es_output_handle,start,wlen,&numwritten,0);
			}
			
			if (toggle_highlight)
			{
				highlighted = !highlighted;

				if (highlighted)
				{
					es_cibuf_attributes = es_highlight_color;
					SetConsoleTextAttribute(es_output_handle,es_highlight_color);
				}
				else
				{
					es_cibuf_attributes = attributes;
					SetConsoleTextAttribute(es_output_handle,attributes);
				}
			}
		}
	}
}

void es_write_QWORD(QWORD value)
{
	wchar_t buf[ES_BUF_SIZE];
	wchar_t *d;
	
	d = buf + ES_BUF_SIZE;
	*--d = 0;

	if (value)
	{
		QWORD i;
		
		i = value;
		
		while(i)
		{
			*--d = (wchar_t)('0' + (i % 10));
			
			i /= 10;
		}
	}
	else
	{
		*--d = '0';
	}	
	
	es_write(d);
}

void es_write_DWORD(DWORD value)
{
	es_write_QWORD(value);
}

void es_write_result_count(DWORD result_count)
{
	es_write_DWORD(result_count);
	es_write(L"\r\n");
}

void es_listresultsW(EVERYTHING_IPC_LIST *list)
{
	if (es_no_result_error)
	{
		if (list->totitems == 0)
		{
			es_ret = ES_ERROR_NO_RESULTS;
		}
	}

	if (es_get_result_count)
	{
		es_write_result_count(list->totitems);
	}
	else
	if (es_get_total_size)
	{
		// version 2 IPC unavailable.
		es_ret = ES_ERROR_IPC;
	}
	else
	{
		DWORD i;
		
		if (es_sort == EVERYTHING_IPC_SORT_PATH_ASCENDING)
		{
			es_sort_list = list;
			qsort(list->items,list->numitems,sizeof(EVERYTHING_IPC_ITEM),es_compare_list_items);
		}
			
		
		for(i=0;i<list->numitems;i++)
		{
			if (list->items[i].flags & EVERYTHING_IPC_DRIVE)
			{
				es_write(EVERYTHING_IPC_ITEMFILENAME(list,&list->items[i]));
				es_write(L"\r\n");
			}
			else
			{
				es_write(EVERYTHING_IPC_ITEMPATH(list,&list->items[i]));
				es_write(L"\\");
				es_write(EVERYTHING_IPC_ITEMFILENAME(list,&list->items[i]));
				es_write(L"\r\n");
			}
		}
	}
	
	PostQuitMessage(0);
}

void es_listresults2W(EVERYTHING_IPC_LIST2 *list,int index_start,int count)
{
	DWORD i;
	EVERYTHING_IPC_ITEM2 *items;
	
	items = (EVERYTHING_IPC_ITEM2 *)(list + 1);

	es_cibuf_y = 0;
	i = index_start;
	
	while(count)
	{
		if (i >= list->numitems)
		{
			break;
		}
	
		es_cibuf_x = -es_cibuf_hscroll;
		
		if ((es_export == ES_EXPORT_CSV) || (es_export == ES_EXPORT_TSV))
		{
			int columni;
			
			for(columni=0;columni<es_numcolumns;columni++)
			{
				void *data;
				
				if (columni)
				{
					es_fwrite((es_export == ES_EXPORT_CSV) ? L"," : L"\t");
				}
				
				data = es_get_column_data(list,i,es_columns[columni]);
				
				if (data)
				{
					switch(es_columns[columni])
					{
						case ES_COLUMN_NAME:
							es_fwrite_csv_string((wchar_t *)((char *)data+sizeof(DWORD)));
							break;
							
						case ES_COLUMN_PATH:
							es_fwrite_csv_string((wchar_t *)((char *)data+sizeof(DWORD)));
							break;
							
						case ES_COLUMN_FILENAME:
							es_fwrite_csv_string((wchar_t *)((char *)data+sizeof(DWORD)));
							break;
							
						case ES_COLUMN_HIGHLIGHTED_NAME:
							es_fwrite_csv_string((wchar_t *)((char *)data+sizeof(DWORD)));
							break;
							
						case ES_COLUMN_HIGHLIGHTED_PATH:
							es_fwrite_csv_string((wchar_t *)((char *)data+sizeof(DWORD)));
							break;
							
						case ES_COLUMN_HIGHLIGHTED_FILENAME:
							es_fwrite_csv_string((wchar_t *)((char *)data+sizeof(DWORD)));
							break;
							
						case ES_COLUMN_EXTENSION:
							es_fwrite_csv_string((wchar_t *)((char *)data+sizeof(DWORD)));
							break;
							
						case ES_COLUMN_SIZE:
						{
							wchar_t column_buf[ES_BUF_SIZE];
							
							if ((items[i].flags & EVERYTHING_IPC_FOLDER) && (*(QWORD *)data == 0xffffffffffffffffI64))
							{
								column_buf[0] = 0;
							}
							else
							{
								es_wstring_print_qword(column_buf,*(QWORD *)data);
							}
							
							es_fwrite(column_buf);
							
							break;
						}

						case ES_COLUMN_DATE_MODIFIED:
						{
							wchar_t column_buf[ES_BUF_SIZE];
							
							es_format_filetime(column_buf,*(QWORD *)data);
							es_fwrite(column_buf);
							break;
						}

						case ES_COLUMN_DATE_CREATED:
						{
							wchar_t column_buf[ES_BUF_SIZE];
							
							es_format_filetime(column_buf,*(QWORD *)data);
							es_fwrite(column_buf);
							break;
						}

						case ES_COLUMN_DATE_ACCESSED:
						{
							wchar_t column_buf[ES_BUF_SIZE];
							
							es_format_filetime(column_buf,*(QWORD *)data);
							es_fwrite(column_buf);
							break;
						}

						case ES_COLUMN_ATTRIBUTES:
						{
							wchar_t column_buf[ES_BUF_SIZE];
							
							es_format_attributes(column_buf,*(DWORD *)data);
							es_fwrite(column_buf);
							break;
						}

						case ES_COLUMN_FILE_LIST_FILENAME:
						{
							es_fwrite_csv_string((wchar_t *)((char *)data+sizeof(DWORD)));
							break;
						}
						
						case ES_COLUMN_RUN_COUNT:
						{
							wchar_t column_buf[ES_BUF_SIZE];
							
							es_wstring_print_qword(column_buf,*(DWORD *)data);
							es_fwrite(column_buf);
							break;
						}	
											
						case ES_COLUMN_DATE_RUN:
						{
							wchar_t column_buf[ES_BUF_SIZE];
							
							es_format_filetime(column_buf,*(QWORD *)data);
							es_fwrite(column_buf);
							break;
						}		
															
						case ES_COLUMN_DATE_RECENTLY_CHANGED:
						{
							wchar_t column_buf[ES_BUF_SIZE];
							
							es_format_filetime(column_buf,*(QWORD *)data);
							es_fwrite(column_buf);
							break;
						}						
					}
				}
			}

			es_fwrite(L"\r\n");
		}
		else
		if (es_export == ES_EXPORT_EFU)
		{
			void *data;
				
			// Filename
			data = es_get_column_data(list,i,ES_COLUMN_FILENAME);
			if (data)
			{
				es_fwrite_csv_string((wchar_t *)((char *)data+sizeof(DWORD)));
			}

			es_fwrite(L",");
			
			// size
			data = es_get_column_data(list,i,ES_COLUMN_SIZE);
			if (data)
			{
				wchar_t column_buf[ES_BUF_SIZE];
				
				if (*(QWORD *)data != 0xffffffffffffffffI64)
				{
					es_wstring_print_qword(column_buf,*(QWORD *)data);
					es_fwrite(column_buf);
				}
			}

			es_fwrite(L",");

			// date modified
			data = es_get_column_data(list,i,ES_COLUMN_DATE_MODIFIED);
			if (data)
			{
				if ((*(QWORD *)data != 0xffffffffffffffffI64))
				{
					wchar_t column_buf[ES_BUF_SIZE];
					
					es_wstring_print_qword(column_buf,*(QWORD *)data);
					es_fwrite(column_buf);
				}
			}

			es_fwrite(L",");

			// date created
			data = es_get_column_data(list,i,ES_COLUMN_DATE_CREATED);
			if (data)
			{
				if ((*(QWORD *)data != 0xffffffffffffffffI64))
				{
					wchar_t column_buf[ES_BUF_SIZE];
					
					es_wstring_print_qword(column_buf,*(QWORD *)data);
					es_fwrite(column_buf);
				}
			}

			es_fwrite(L",");

			// date created
			data = es_get_column_data(list,i,ES_COLUMN_ATTRIBUTES);
			if (data)
			{
				wchar_t column_buf[ES_BUF_SIZE];
						
				es_wstring_print_qword(column_buf,*(DWORD *)data);
				es_fwrite(column_buf);
			}
			else
			{
				DWORD file_attributes;
				wchar_t column_buf[ES_BUF_SIZE];
				
				// add file/folder attributes.
				if (items[i].flags & EVERYTHING_IPC_FOLDER)
				{
					file_attributes = FILE_ATTRIBUTE_DIRECTORY;
				}
				else
				{
					file_attributes = 0;
				}

				es_wstring_print_qword(column_buf,file_attributes);
				es_fwrite(column_buf);
			}

			es_fwrite(L"\r\n");
		}
		else
		if ((es_export == ES_EXPORT_TXT) || (es_export == ES_EXPORT_M3U) || (es_export == ES_EXPORT_M3U8))
		{
			void *data;
			
			data = es_get_column_data(list,i,ES_COLUMN_FILENAME);
			
			if (es_double_quote)
			{
				es_fwrite(L"\"");
			}
			
			if (data)
			{
				es_fwrite((wchar_t *)((char *)data+sizeof(DWORD)));
			}

			if (es_double_quote)
			{
				es_fwrite(L"\"");
			}
			
			es_fwrite(L"\r\n");
		}
		else
		{
			int columni;
			int did_set_color;
			int tot_column_text_len;
			int tot_column_width;
			
			tot_column_text_len = 0;
			tot_column_width = 0;

			for(columni=0;columni<es_numcolumns;columni++)
			{
				void *data;
				const wchar_t *column_text;
				int column_is_highlighted;
				int column_is_double_quote;
				wchar_t column_buf[ES_BUF_SIZE];
				
				if (columni)
				{
					es_write(L" ");
					tot_column_text_len++;
					tot_column_width++;
				}
				
				data = es_get_column_data(list,i,es_columns[columni]);

				es_is_last_color = 0;
				did_set_color = 0;
				
				column_text = L"";
				column_is_highlighted = 0;
				column_is_double_quote = 0;

				if (data)
				{
					if (!es_output_is_file)
					{
						if (es_column_color_is_valid[es_columns[columni]])
						{
							es_cibuf_attributes = es_column_color[es_columns[columni]];
							SetConsoleTextAttribute(es_output_handle,es_column_color[es_columns[columni]]);

							es_last_color = es_column_color[es_columns[columni]];
							es_is_last_color = 1;

							did_set_color = 1;
						}
					}

					switch(es_columns[columni])
					{
						case ES_COLUMN_NAME:
							column_text = (wchar_t *)((char *)data+sizeof(DWORD));
							column_is_double_quote = es_double_quote;
							break;
							
						case ES_COLUMN_PATH:
							column_text = (wchar_t *)((char *)data+sizeof(DWORD));
							column_is_double_quote = es_double_quote;
							break;
							
						case ES_COLUMN_FILENAME:
							column_text = (wchar_t *)((char *)data+sizeof(DWORD));
							column_is_double_quote = es_double_quote;
							break;
							
						case ES_COLUMN_HIGHLIGHTED_NAME:
							column_text = (wchar_t *)((char *)data+sizeof(DWORD));
							column_is_highlighted = 1;
							column_is_double_quote = es_double_quote;
							break;
							
						case ES_COLUMN_HIGHLIGHTED_PATH:
							column_text = (wchar_t *)((char *)data+sizeof(DWORD));
							column_is_highlighted = 1;
							column_is_double_quote = es_double_quote;
							break;
							
						case ES_COLUMN_HIGHLIGHTED_FILENAME:
							column_text = (wchar_t *)((char *)data+sizeof(DWORD));
							column_is_highlighted = 1;
							column_is_double_quote = es_double_quote;
							break;
							
						case ES_COLUMN_EXTENSION:
						{
							column_text = (wchar_t *)((char *)data+sizeof(DWORD));
							break;
						}
							
						case ES_COLUMN_SIZE:
						{
							
							if ((items[i].flags & EVERYTHING_IPC_FOLDER) && (*(QWORD *)data == 0xffffffffffffffffI64))
							{
								es_format_dir(column_buf);
							}
							else
							{
								es_format_size(column_buf,*(QWORD *)data);
							}

							column_text = column_buf;

							break;
						}

						case ES_COLUMN_DATE_CREATED:
						{
							es_format_filetime(column_buf,*(QWORD *)data);

							column_text = column_buf;
							break;
						}
						
						case ES_COLUMN_DATE_MODIFIED:
						{
							es_format_filetime(column_buf,*(QWORD *)data);
							column_text = column_buf;
							break;
						}
						
						case ES_COLUMN_DATE_ACCESSED:
						{
							es_format_filetime(column_buf,*(QWORD *)data);
							column_text = column_buf;
							break;
						}
						
						case ES_COLUMN_ATTRIBUTES:
						{
							es_format_attributes(column_buf,*(DWORD *)data);
							column_text = column_buf;
							break;
						}

						case ES_COLUMN_FILE_LIST_FILENAME:
						{
							column_text = (wchar_t *)((char *)data+sizeof(DWORD));
							break;
						}
						
						case ES_COLUMN_RUN_COUNT:
						{
							es_format_run_count(column_buf,*(DWORD *)data);
							column_text = column_buf;
							break;
						}	
											
						case ES_COLUMN_DATE_RUN:
						{
							es_format_filetime(column_buf,*(QWORD *)data);
							column_text = column_buf;
							break;
						}		
															
						case ES_COLUMN_DATE_RECENTLY_CHANGED:
						{
							es_format_filetime(column_buf,*(QWORD *)data);
							column_text = column_buf;
							break;
						}
					}

					//es_column_widths
					{
						int column_text_len;
						int fill;
						int column_width;
						int is_right_aligned;
						
						is_right_aligned = es_column_is_right_aligned[es_columns[columni]];
						
						if (is_right_aligned)
						{
							// check for right aligned dir
							// and make it left aligned.
							if (*column_text == '<')
							{
								is_right_aligned = 0;
							}
						}
						
						if (column_is_highlighted)
						{
							column_text_len = es_wstring_highlighted_length(column_text);
						}
						else
						{
							column_text_len = es_wstring_length(column_text);
						}
						
						if (column_is_double_quote)
						{
							column_text_len += 2;
						}
						
						column_width = es_column_widths[es_columns[columni]];
						
						// left fill
						if (is_right_aligned)
						{
							if (tot_column_width + column_width > (tot_column_text_len + column_text_len))
							{
								int fillch;
								
								fill = tot_column_width + column_width - (tot_column_text_len + column_text_len);

								fillch = ' ';
								
								if (!es_digit_grouping)
								{
									if (es_columns[columni] == ES_COLUMN_SIZE)
									{
										fillch = es_size_leading_zero ? '0' : ' ';
									}
									else
									if (es_columns[columni] == ES_COLUMN_RUN_COUNT)
									{
										fillch = es_run_count_leading_zero ? '0' : ' ';
									}
								}
							
								// left spaces.
								es_fill(fill,fillch);
								tot_column_text_len += fill;
							}
						}

						if (column_is_double_quote)
						{
							es_write(L"\"");
						}

						// write text.
						if (column_is_highlighted)
						{
							es_write_highlighted(column_text);
						}
						else
						{
							es_write(column_text);
						}
				
						if (column_is_double_quote)
						{
							es_write(L"\"");
						}

						// right fill
						if (!is_right_aligned)
						{
							if (tot_column_width + column_width > (tot_column_text_len + column_text_len))
							{
								fill = tot_column_width + column_width - (tot_column_text_len + column_text_len);

								// dont right fill last column.
								if (columni != es_numcolumns - 1)
								{
									// right spaces.
									es_fill(fill,' ');
									tot_column_text_len += fill;
								}
							}
						}
							
									
						tot_column_text_len += column_text_len;
						tot_column_width += es_column_widths[es_columns[columni]];
					}

					// restore color
					if (did_set_color)
					{
						es_cibuf_attributes = es_default_attributes;
						SetConsoleTextAttribute(es_output_handle,es_default_attributes);
					}
				}
			}
			
			if (es_pause)
			{
				// fill right side of screen
				if (es_cibuf)
				{
					int wlen;
					
					if (es_cibuf_x)
					{
						if (es_cibuf_x + es_cibuf_hscroll > es_max_wide)
						{
							es_max_wide = es_cibuf_x + es_cibuf_hscroll;
						}
					}
			
					wlen = es_console_wide - es_cibuf_x;
					
					if (wlen > 0)
					{
						int bufi;
						
						for(bufi=0;bufi<wlen;bufi++)
						{
							if (bufi + es_cibuf_x >= es_console_wide)
							{
								break;
							}

							if (bufi + es_cibuf_x >= 0)
							{
								es_cibuf[bufi+es_cibuf_x].Attributes = es_cibuf_attributes;
								es_cibuf[bufi+es_cibuf_x].Char.UnicodeChar = ' ';
							}
						}
					}
					
					// draw buffer line.
					{
						COORD buf_size;
						COORD buf_pos;
						SMALL_RECT write_rect;
						
						buf_size.X = es_console_wide;
						buf_size.Y = 1;
						
						buf_pos.X = 0;
						buf_pos.Y = 0;
						
						write_rect.Left = es_console_window_x;
						write_rect.Top = es_console_window_y + es_cibuf_y;
						write_rect.Right = es_console_window_x + es_console_wide;
						write_rect.Bottom = es_console_window_y + es_cibuf_y + 1;
						
						WriteConsoleOutput(es_output_handle,es_cibuf,buf_size,buf_pos,&write_rect);
						
						es_cibuf_x += wlen;				
					}
				}			
			}
			else
			{
				es_write(L"\r\n");
			}
		}

		es_cibuf_y++;
		count--;
		i++;
	}
}

void es_write_total_size(EVERYTHING_IPC_LIST2 *list,int count)
{
	DWORD i;
	EVERYTHING_IPC_ITEM2 *items;
	QWORD total_size;
	
	items = (EVERYTHING_IPC_ITEM2 *)(list + 1);

	i = 0;
	total_size = 0;
	
	while(count)
	{
		void *data;
		
		if (i >= list->numitems)
		{
			break;
		}
	
		data = es_get_column_data(list,i,ES_COLUMN_SIZE);

		if (data)
		{
			// dont count folders
			if (!(items[i].flags & EVERYTHING_IPC_FOLDER))
			{
				total_size += *(QWORD *)data;
			}
		}

		count--;
		i++;
	}

	es_write_QWORD(total_size);
	es_write(L"\r\n");
}

// custom window proc
LRESULT __stdcall es_window_proc(HWND hwnd,UINT msg,WPARAM wParam,LPARAM lParam)
{
	switch(msg)
	{
		case WM_COPYDATA:
		{
			COPYDATASTRUCT *cds = (COPYDATASTRUCT *)lParam;
			
			switch(cds->dwData)
			{
				case ES_COPYDATA_IPCTEST_QUERYCOMPLETEW:
					es_listresultsW((EVERYTHING_IPC_LIST *)cds->lpData);
					return TRUE;
					
				case ES_COPYDATA_IPCTEST_QUERYCOMPLETE2W:
				{
					if (es_no_result_error)
					{
						if (((EVERYTHING_IPC_LIST2 *)cds->lpData)->totitems == 0)
						{
							es_ret = ES_ERROR_NO_RESULTS;
						}
					}
				
					if (es_get_result_count)
					{
						es_write_result_count(((EVERYTHING_IPC_LIST2 *)cds->lpData)->totitems);
					}
					else
					if (es_get_total_size)
					{
						es_write_total_size((EVERYTHING_IPC_LIST2 *)cds->lpData,((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems);
					}
					else
					if ((int)((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems)
					{
						if (es_pause)
						{
							HANDLE std_input_handle;
							int start_index;
							int last_start_index;
							int last_hscroll;
							int info_line;
							
							info_line = es_console_high - 1;
							
							if (info_line > (int)((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems)
							{
								info_line = (int)((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems;
							}
							
							// push lines above up.
							{
								int i;
								
								if (es_console_window_y + info_line + 1 > es_console_size_high)
								{
									for(i=0;i<(es_console_window_y + info_line + 1) - es_console_size_high;i++)
									{
										DWORD numwritten;
										
										WriteFile(es_output_handle,"\r\n",2,&numwritten,0);
									}
																
									es_console_window_y = es_console_size_high - (info_line + 1);
								}
							}
							
							std_input_handle = GetStdHandle(STD_INPUT_HANDLE);

							es_cibuf = es_alloc(es_console_wide * sizeof(CHAR_INFO));
							
							// set cursor pos to the bottom of the screen
							{
								COORD cur_pos;
								cur_pos.X = es_console_window_x;
								cur_pos.Y = es_console_window_y + info_line;
								
								SetConsoleCursorPosition(es_output_handle,cur_pos);
							}

							// write out some basic usage at the bottom
							{
								DWORD numwritten;
						
								WriteFile(es_output_handle,ES_PAUSE_TEXT,(int)strlen(ES_PAUSE_TEXT),&numwritten,0);
							}
						
							start_index = 0;
							last_start_index = 0;
							last_hscroll = 0;
						
							for(;;)
							{
								INPUT_RECORD ir;
								DWORD numread;

								es_listresults2W((EVERYTHING_IPC_LIST2 *)cds->lpData,start_index,es_console_high-1);
								
								// is everything is shown?
								if (es_max_wide <= es_console_wide)
								{
									if (((int)((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems) < es_console_high)
									{
										goto exit;
									}
								}
								
start:
								
								for(;;)
								{
									if (PeekConsoleInput(std_input_handle,&ir,1,&numread)) 
									{
										if (numread)
										{
											ReadConsoleInput(std_input_handle,&ir,1,&numread);

											if ((ir.EventType == KEY_EVENT) && (ir.Event.KeyEvent.bKeyDown))
											{
												if (ir.Event.KeyEvent.wVirtualKeyCode == VK_RETURN)
												{
													if ((int)((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems < es_console_high + 1)
													{
														goto exit;
													}
													if (start_index == (int)((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems - es_console_high + 1)
													{
														goto exit;
													}
													start_index += 1;
													break;
												}
												else
												if (ir.Event.KeyEvent.wVirtualKeyCode == VK_SPACE)
												{
													if ((int)((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems < es_console_high + 1)
													{
														goto exit;
													}
													
													if (start_index == (int)((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems - es_console_high + 1)
													{
														goto exit;
													}
													
													// fall through
												}
												else
												if (ir.Event.KeyEvent.wVirtualKeyCode == VK_RIGHT)
												{
													es_cibuf_hscroll += 5;
													
													if (es_max_wide > es_console_wide)
													{
														if (es_cibuf_hscroll > es_max_wide - es_console_wide)
														{
															es_cibuf_hscroll = es_max_wide - es_console_wide;
														}
													}
													else
													{
														es_cibuf_hscroll = 0;
													}
													break;
												}
												else
												if (ir.Event.KeyEvent.wVirtualKeyCode == VK_LEFT)
												{
													es_cibuf_hscroll -= 5;
													if (es_cibuf_hscroll < 0)
													{
														es_cibuf_hscroll = 0;
													}
													break;
												}
												else
												if (ir.Event.KeyEvent.wVirtualKeyCode == VK_UP)
												{
													start_index -= 1;
													break;
												}
												else
												if (ir.Event.KeyEvent.wVirtualKeyCode == VK_DOWN)
												{
													start_index += 1;
													break;
												}
												else
												if (ir.Event.KeyEvent.wVirtualKeyCode == VK_PRIOR)
												{
													start_index -= es_console_high - 1;
													break;
												}
												else
												if (ir.Event.KeyEvent.wVirtualKeyCode == VK_HOME)
												{
													start_index = 0;
													break;
												}
												else
												if (ir.Event.KeyEvent.wVirtualKeyCode == VK_END)
												{
													start_index = ((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems - es_console_high + 1;
													break;
												}
												else
												if ((ir.Event.KeyEvent.wVirtualKeyCode == 'Q') || (ir.Event.KeyEvent.wVirtualKeyCode == VK_ESCAPE))
												{
													goto exit;
												}
												else
												if (es_is_valid_key(&ir))
												{
													start_index += es_console_high - 1;
													break;
												}
											}
										}
									}
							
									Sleep(1);
								}
								
								// clip start index.
								if (start_index < 0)
								{
									start_index = 0;
								}
								else
								if ((int)((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems > es_console_high + 1)
								{
									if (start_index > (int)((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems - es_console_high + 1)
									{
										start_index = ((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems - es_console_high + 1;
									}
								}
								else
								{
									start_index = 0;
								}
								
								if (last_start_index != start_index)
								{
									last_start_index = start_index;
								}
								else
								if (last_hscroll != es_cibuf_hscroll)
								{
									last_hscroll = es_cibuf_hscroll;
								}
								else
								{
									goto start;
								}
							}
							
	exit:						
							
							// remove text.
							if (es_cibuf)
							{
								// set cursor pos to the bottom of the screen
								{
									COORD cur_pos;
									cur_pos.X = es_console_window_x;
									cur_pos.Y = es_console_window_y + info_line;
									
									SetConsoleCursorPosition(es_output_handle,cur_pos);
								}

								// write out some basic usage at the bottom
								{
									DWORD numwritten;
							
									WriteFile(es_output_handle,ES_BLANK_PAUSE_TEXT,(int)strlen(ES_BLANK_PAUSE_TEXT),&numwritten,0);
								}
							
								// reset cursor pos.
								{
									COORD cur_pos;
									cur_pos.X = es_console_window_x;
									cur_pos.Y = es_console_window_y + info_line;
									
									SetConsoleCursorPosition(es_output_handle,cur_pos);
								}
								
								// free
								es_free(es_cibuf);
							}								
						}
						else
						{
							es_listresults2W((EVERYTHING_IPC_LIST2 *)cds->lpData,0,((EVERYTHING_IPC_LIST2 *)cds->lpData)->numitems);
						}
					}
	
					PostQuitMessage(0);
				
					return TRUE;
				}
			}
			
			break;
		}
	}
	
	return DefWindowProc(hwnd,msg,wParam,lParam);
}

void es_help(void)
{
	// Help from NotNull
	es_write(L"ES " ES_VERSION L"\r\n");
	es_write(L"ES is a command line interface to search Everything from a command prompt.\r\n");
	es_write(L"ES uses the Everything search syntax.\r\n");
	es_write(L"\r\n");
	es_write(L"Usage: es.exe [options] search text\r\n");
	es_write(L"Example: ES  Everything ext:exe;ini \r\n");
	es_write(L"\r\n");
	es_write(L"\r\n");
	es_write(L"Search options\r\n");
	es_write(L"   -r <search>, -regex <search>\r\n");
	es_write(L"        Search using regular expressions.\r\n");
	es_write(L"   -i, -case\r\n");
	es_write(L"        Match case.\r\n");
	es_write(L"   -w, -ww, -whole-word, -whole-words\r\n");
	es_write(L"        Match whole words.\r\n");
	es_write(L"   -p, -match-path\r\n");
	es_write(L"        Match full path and file name.\r\n");
	es_write(L"   -a, -diacritics\r\n");
	es_write(L"        Match diacritical marks.\r\n");
	es_write(L"\r\n");
	es_write(L"   -o <offset>, -offset <offset>\r\n");
	es_write(L"        Show results starting from offset.\r\n");
	es_write(L"   -n <num>, -max-results <num>\r\n");
	es_write(L"        Limit the number of results shown to <num>.\r\n");
	es_write(L"\r\n");
	es_write(L"   -path <path>\r\n");
	es_write(L"        Search for subfolders and files in path.\r\n");
	es_write(L"   -parent-path <path>\r\n");
	es_write(L"        Search for subfolders and files in the parent of path.\r\n");
	es_write(L"   -parent <path>\r\n");
	es_write(L"        Search for files with the specified parent path.\r\n");
	es_write(L"\r\n");
	es_write(L"   /ad\r\n");
	es_write(L"        Folders only.\r\n");
	es_write(L"   /a-d\r\n");
	es_write(L"        Files only.\r\n");
	es_write(L"   /a[RHSDAVNTPLCOIE]\r\n");
	es_write(L"        DIR style attributes search.\r\n");
	es_write(L"        R = Read only.\r\n");
	es_write(L"        H = Hidden.\r\n");
	es_write(L"        S = System.\r\n");
	es_write(L"        D = Directory.\r\n");
	es_write(L"        A = Archive.\r\n");
	es_write(L"        V = Device.\r\n");
	es_write(L"        N = Normal.\r\n");
	es_write(L"        T = Temporary.\r\n");
	es_write(L"        P = Sparse file.\r\n");
	es_write(L"        L = Reparse point.\r\n");
	es_write(L"        C = Compressed.\r\n");
	es_write(L"        O = Offline.\r\n");
	es_write(L"        I = Not content indexed.\r\n");
	es_write(L"        E = Encrypted.\r\n");
	es_write(L"        - = Prefix a flag with - to exclude.\r\n");
	es_write(L"\r\n");
	es_write(L"\r\n");
	es_write(L"Sort options\r\n");
	es_write(L"   -s\r\n");
	es_write(L"        sort by full path.\r\n");
	es_write(L"   -sort <name[-ascending|-descending]>, -sort-<name>[-ascending|-descending]\r\n");
	es_write(L"        Set sort\r\n");
	es_write(L"        name=name|path|size|extension|date-created|date-modified|date-accessed|\r\n");
	es_write(L"        attributes|file-list-file-name|run-count|date-recently-changed|date-run\r\n");
	es_write(L"   -sort-ascending, -sort-descending\r\n");
	es_write(L"        Set sort order\r\n");
	es_write(L"\r\n");
	es_write(L"   /on, /o-n, /os, /o-s, /oe, /o-e, /od, /o-d\r\n");
	es_write(L"        DIR style sorts.\r\n");
	es_write(L"        N = Name.\r\n");
	es_write(L"        S = Size.\r\n");
	es_write(L"        E = Extension.\r\n");
	es_write(L"        D = Date modified.\r\n");
	es_write(L"        - = Sort in descending order.\r\n");
	es_write(L"\r\n");
	es_write(L"\r\n");
	es_write(L"Display options\r\n");
	es_write(L"   -name\r\n");
	es_write(L"   -path-column\r\n");
	es_write(L"   -full-path-and-name, -filename-column\r\n");
	es_write(L"   -extension, -ext\r\n");
	es_write(L"   -size\r\n");
	es_write(L"   -date-created, -dc\r\n");
	es_write(L"   -date-modified, -dm\r\n");
	es_write(L"   -date-accessed, -da\r\n");
	es_write(L"   -attributes, -attribs, -attrib\r\n");
	es_write(L"   -file-list-file-name\r\n");
	es_write(L"   -run-count\r\n");
	es_write(L"   -date-run\r\n");
	es_write(L"   -date-recently-changed, -rc\r\n");
	es_write(L"        Show the specified column.\r\n");
	es_write(L"        \r\n");
	es_write(L"   -highlight\r\n");
	es_write(L"        Highlight results.\r\n");
	es_write(L"   -highlight-color <color>\r\n");
	es_write(L"        Highlight color 0-255.\r\n");
	es_write(L"\r\n");
	es_write(L"   -csv\r\n");
	es_write(L"   -efu\r\n");
	es_write(L"   -txt\r\n");
	es_write(L"   -m3u\r\n");
	es_write(L"   -m3u8\r\n");
	es_write(L"   -tsv\r\n");
	es_write(L"        Change display format.\r\n");
	es_write(L"\r\n");
	es_write(L"   -size-format <format>\r\n");
	es_write(L"        0=auto, 1=Bytes, 2=KB, 3=MB.\r\n");
	es_write(L"   -date-format <format>\r\n");
	es_write(L"        0=auto, 1=ISO-8601, 2=FILETIME, 3=ISO-8601(UTC)\r\n");
	es_write(L"\r\n");
	es_write(L"   -filename-color <color>\r\n");
	es_write(L"   -name-color <color>\r\n");
	es_write(L"   -path-color <color>\r\n");
	es_write(L"   -extension-color <color>\r\n");
	es_write(L"   -size-color <color>\r\n");
	es_write(L"   -date-created-color <color>, -dc-color <color>\r\n");
	es_write(L"   -date-modified-color <color>, -dm-color <color>\r\n");
	es_write(L"   -date-accessed-color <color>, -da-color <color>\r\n");
	es_write(L"   -attributes-color <color>\r\n");
	es_write(L"   -file-list-filename-color <color>\r\n");
	es_write(L"   -run-count-color <color>\r\n");
	es_write(L"   -date-run-color <color>\r\n");
	es_write(L"   -date-recently-changed-color <color>, -rc-color <color>\r\n");
	es_write(L"        Set the column color 0-255.\r\n");
	es_write(L"\r\n");
	es_write(L"   -filename-width <width>\r\n");
	es_write(L"   -name-width <width>\r\n");
	es_write(L"   -path-width <width>\r\n");
	es_write(L"   -extension-width <width>\r\n");
	es_write(L"   -size-width <width>\r\n");
	es_write(L"   -date-created-width <width>, -dc-width <width>\r\n");
	es_write(L"   -date-modified-width <width>, -dm-width <width>\r\n");
	es_write(L"   -date-accessed-width <width>, -da-width <width>\r\n");
	es_write(L"   -attributes-width <width>\r\n");
	es_write(L"   -file-list-filename-width <width>\r\n");
	es_write(L"   -run-count-width <width>\r\n");
	es_write(L"   -date-run-width <width>\r\n");
	es_write(L"   -date-recently-changed-width <width>, -rc-width <width>\r\n");
	es_write(L"        Set the column width 0-200.\r\n");
	es_write(L"\r\n");
	es_write(L"   -no-digit-grouping\r\n");
	es_write(L"        Don't group numbers with commas.\r\n");
	es_write(L"   -size-leading-zero\r\n");
	es_write(L"   -run-count-leading-zero\r\n");
	es_write(L"        Format the number with leading zeros, use with -no-digit-grouping.\r\n");
	es_write(L"   -double-quote\r\n");
	es_write(L"        Wrap paths and filenames with double quotes.\r\n");
	es_write(L"\r\n");
	es_write(L"\r\n");
	es_write(L"Export options\r\n");
	es_write(L"   -export-csv <out.csv>\r\n");
	es_write(L"   -export-efu <out.efu>\r\n");
	es_write(L"   -export-txt <out.txt>\r\n");
	es_write(L"   -export-m3u <out.m3u>\r\n");
	es_write(L"   -export-m3u8 <out.m3u8>\r\n");
	es_write(L"   -export-tsv <out.txt>\r\n");
	es_write(L"        Export to a file using the specified layout.\r\n");
	es_write(L"   -no-header\r\n");
	es_write(L"        Do not output a column header for CSV, EFU and TSV files.\r\n");
	es_write(L"   -utf8-bom\r\n");
	es_write(L"        Store a UTF-8 byte order mark at the start of the exported file.\r\n");
	es_write(L"\r\n");
	es_write(L"\r\n");
	es_write(L"General options\r\n");
	es_write(L"   -h, -help\r\n");
	es_write(L"        Display this help.\r\n");
	es_write(L"\r\n");
	es_write(L"   -instance <name>\r\n");
	es_write(L"        Connect to the unique Everything instance name.\r\n");
	es_write(L"   -ipc1, -ipc2\r\n");
	es_write(L"        Use IPC version 1 or 2.\r\n");
	es_write(L"   -pause, -more\r\n");
	es_write(L"        Pause after each page of output.\r\n");
	es_write(L"   -hide-empty-search-results\r\n");
	es_write(L"        Don't show any results when there is no search.\r\n");
	es_write(L"   -empty-search-help\r\n");
	es_write(L"        Show help when no search is specified.\r\n");
	es_write(L"   -timeout <milliseconds>\r\n");
	es_write(L"        Timeout after the specified number of milliseconds to wait for\r\n");
	es_write(L"        the Everything database to load before sending a query.\r\n");
	es_write(L"        \r\n");
	es_write(L"   -set-run-count <filename> <count>\r\n");
	es_write(L"        Set the run count for the specified filename.\r\n");
	es_write(L"   -inc-run-count <filename>\r\n");
	es_write(L"        Increment the run count for the specified filename by one.\r\n");
	es_write(L"   -get-run-count <filename>\r\n");
	es_write(L"        Display the run count for the specified filename.\r\n");
	es_write(L"   -get-result-count\r\n");
	es_write(L"        Display the result count for the specified search.\r\n");
	es_write(L"   -get-total-size\r\n");
	es_write(L"        Display the total result size for the specified search.\r\n");
	es_write(L"   -save-settings, -clear-settings\r\n");
	es_write(L"        Save or clear settings.\r\n");
	es_write(L"   -version\r\n");
	es_write(L"        Display ES major.minor.revision.build version and exit.\r\n");
	es_write(L"   -get-everything-version\r\n");
	es_write(L"        Display Everything major.minor.revision.build version and exit.\r\n");
	es_write(L"   -exit\r\n");
	es_write(L"        Exit Everything.\r\n");
	es_write(L"        Returns after Everything process closes.\r\n");
	es_write(L"   -save-db\r\n");
	es_write(L"        Save the Everything database to disk.\r\n");
	es_write(L"        Returns after saving completes.\r\n");
	es_write(L"   -reindex\r\n");
	es_write(L"        Force Everything to reindex.\r\n");
	es_write(L"        Returns after indexing completes.\r\n");
	es_write(L"   -no-result-error\r\n");
	es_write(L"        Set the error level if no results are found.\r\n");
	es_write(L"\r\n");
	es_write(L"\r\n");
	es_write(L"Notes \r\n");
	es_write(L"    Internal -'s in options can be omitted, eg: -nodigitgrouping\r\n");
	es_write(L"    Switches can also start with a /\r\n");
	es_write(L"    Use double quotes to escape spaces and switches.\r\n");
	es_write(L"    Switches can be disabled by prefixing them with no-, eg: -no-size.\r\n");
	es_write(L"    Use a ^ prefix or wrap with double quotes (\") to escape \\ & | > < ^\r\n");
}

// main entry
int main(int argc,char **argv)
{
	WNDCLASSEX wcex;
	HWND hwnd;
	MSG msg;
	int ret;
	wchar_t *d;
	wchar_t *e;
	wchar_t *s;
	const wchar_t *command_line;
	int perform_search;
	
	perform_search = 1;
	
	ZeroMemory(&wcex,sizeof(wcex));
	wcex.cbSize = sizeof(wcex);
	
	es_output_handle = GetStdHandle(STD_OUTPUT_HANDLE);

	// get console info.
	{
		CONSOLE_SCREEN_BUFFER_INFO csbi;
		DWORD mode;

		if (GetConsoleMode(es_output_handle,&mode))
		{
			if (GetConsoleScreenBufferInfo(es_output_handle,&csbi))
			{
				es_console_wide = csbi.srWindow.Right - csbi.srWindow.Left + 1;
				es_console_high = csbi.srWindow.Bottom - csbi.srWindow.Top + 1;

				es_console_window_x = csbi.srWindow.Left;
				es_console_window_y = csbi.dwCursorPosition.Y;
				
				if ((!es_console_wide) || (!es_console_high))
				{
					es_console_wide = csbi.dwSize.X;
					es_console_high = csbi.dwSize.Y;
				}
				
				es_console_size_high = csbi.dwSize.Y;
				
				es_cibuf_attributes = csbi.wAttributes;
				es_default_attributes = csbi.wAttributes;
				es_output_is_file = 0;
			}
		}
	}
			
	if (!GetClassInfoEx(GetModuleHandle(0),TEXT("IPCTEST"),&wcex))
	{
		ZeroMemory(&wcex,sizeof(wcex));
		wcex.cbSize = sizeof(wcex);
		wcex.hInstance = GetModuleHandle(0);
		wcex.lpfnWndProc = es_window_proc;
		wcex.lpszClassName = TEXT("IPCTEST");
		
		if (!RegisterClassEx(&wcex))
		{
			es_fatal(ES_ERROR_REGISTER_WINDOW_CLASS);
		}
	}
	
	hwnd = CreateWindow(TEXT("IPCTEST"),TEXT(""),0,0,0,0,0,0,0,GetModuleHandle(0),0);
	if (!hwnd)
	{
		es_fatal(ES_ERROR_CREATE_WINDOW);
	}

	// allow the everything window to send a reply.
	es_user32_hdll = LoadLibraryA("user32.dll");
	
	if (es_user32_hdll)
	{
		es_pChangeWindowMessageFilterEx = (BOOL (WINAPI *)(HWND hWnd,UINT message,DWORD action,PCHANGEFILTERSTRUCT pChangeFilterStruct))GetProcAddress(es_user32_hdll,"ChangeWindowMessageFilterEx");

		if (es_pChangeWindowMessageFilterEx)
		{
			es_pChangeWindowMessageFilterEx(hwnd,WM_COPYDATA,MSGFLT_ALLOW,0);
		}
	}
	
	es_instance = es_alloc(ES_BUF_SIZE);
	es_search = es_alloc(ES_SEARCH_BUF_SIZE * sizeof(wchar_t));
	es_filter = es_alloc(ES_FILTER_BUF_SIZE * sizeof(wchar_t));
	
	*es_instance = 0;
	*es_filter = 0;

	d = es_search;

	// allow room for null terminator
	e = es_search + ES_SEARCH_BUF_SIZE - 1;

	// load default settings.	
	es_load_settings();
	
	command_line = GetCommandLine();
	
/*
	// code page test
	
	printf("CP %u\n",GetConsoleCP());
	printf("CP output %u\n",GetConsoleOutputCP());
	
	MessageBox(0,command_line,L"command line",MB_OK);
	return 0;
//	printf("command line %S\n",command_line);
*/
	// expect the executable name in the first argv.
	if (command_line)
	{
		command_line = es_get_argv(command_line);
	}
	
	if (command_line)
	{
		command_line = es_get_argv(command_line);
		
		if (command_line)
		{
			for(;;)
			{
				if (es_check_param(es_argv,L"-set-run-count"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_run_history_size = sizeof(EVERYTHING_IPC_RUN_HISTORY) + ((es_wstring_length(es_argv) + 1) * sizeof(wchar_t));
					es_run_history_data = es_alloc(es_run_history_size);
					es_wstring_copy((wchar_t *)(((EVERYTHING_IPC_RUN_HISTORY *)es_run_history_data)+1),es_argv);
					
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					((EVERYTHING_IPC_RUN_HISTORY *)es_run_history_data)->run_count = es_wstring_to_int(es_argv);
					es_run_history_command = EVERYTHING_IPC_COPYDATA_SET_RUN_COUNTW;
				}
				else			
				if (es_check_param(es_argv,L"-inc-run-count"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_run_history_size = (es_wstring_length(es_argv) + 1) * sizeof(wchar_t);
					es_run_history_data = es_wstring_alloc(es_argv);
					es_run_history_command = EVERYTHING_IPC_COPYDATA_INC_RUN_COUNTW;
				}
				else			
				if (es_check_param(es_argv,L"-get-run-count"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_run_history_size = (es_wstring_length(es_argv) + 1) * sizeof(wchar_t);
					es_run_history_data = es_wstring_alloc(es_argv);
					es_run_history_command = EVERYTHING_IPC_COPYDATA_GET_RUN_COUNTW;
				}
				else
				if ((es_check_param(es_argv,L"r")) || (es_check_param(es_argv,L"regex")))
				{
					command_line = es_get_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
							
					if ((d != es_search) && (d < e)) *d++ = ' ';
					
					s = L"regex:";
					while(*s)
					{
						if (d < e) *d++ = *s;
						s++;
					}
										
					s = es_argv;
					while(*s)
					{
						if (d < e) *d++ = *s;
						s++;
					}		
				}
				else
				if ((es_check_param(es_argv,L"i")) || (es_check_param(es_argv,L"case")))
				{
					es_match_case = 1;
				}
				else
				if ((es_check_param(es_argv,L"no-i")) || (es_check_param(es_argv,L"no-case")))
				{
					es_match_case = 0;
				}
				else
				if ((es_check_param(es_argv,L"a")) || (es_check_param(es_argv,L"diacritics")))
				{
					es_match_diacritics = 1;
				}
				else
				if ((es_check_param(es_argv,L"no-a")) || (es_check_param(es_argv,L"no-diacritics")))
				{
					es_match_diacritics = 0;
				}
				else
				if (es_check_param(es_argv,L"instance"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_wstring_copy(es_instance,es_argv);
				}
				else
				if ((es_check_param(es_argv,L"exit")) || (es_check_param(es_argv,L"quit")))
				{
					es_exit_everything = 1;
				}
				else
				if ((es_check_param(es_argv,L"re-index")) || (es_check_param(es_argv,L"re-build")) || (es_check_param(es_argv,L"update")))
				{
					es_reindex = 1;
				}
				else
				if (es_check_param(es_argv,L"save-db"))
				{
					es_save_db = 1;
				}
				else
				if (es_check_param(es_argv,L"connect"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					if (es_connect)
					{
						es_free(es_connect);
					}
					
					es_connect = es_wstring_alloc(es_argv);
				}
				else
				if (es_check_param(es_argv,L"highlight-color"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_highlight_color = es_wstring_to_int(es_argv);
				}
				else
				if (es_check_param(es_argv,L"highlight"))
				{
					es_highlight = 1;
				}
				else			
				if (es_check_param(es_argv,L"no-highlight"))
				{
					es_highlight = 0;
				}
				else
				if (es_check_param(es_argv,L"m3u"))
				{
					es_export = ES_EXPORT_M3U;
				}
				else				
				if (es_check_param(es_argv,L"export-m3u"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_export_file = CreateFile(es_argv,GENERIC_WRITE,FILE_SHARE_READ,0,CREATE_ALWAYS,0,0);
					if (es_export_file != INVALID_HANDLE_VALUE)
					{
						es_export = ES_EXPORT_M3U;
					}
					else
					{
						es_fatal(ES_ERROR_CREATE_FILE);
					}
				}
				else					
				if (es_check_param(es_argv,L"m3u8"))
				{
					es_export = ES_EXPORT_M3U8;
				}
				else				
				if (es_check_param(es_argv,L"export-m3u8"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_export_file = CreateFile(es_argv,GENERIC_WRITE,FILE_SHARE_READ,0,CREATE_ALWAYS,0,0);
					if (es_export_file != INVALID_HANDLE_VALUE)
					{
						es_export = ES_EXPORT_M3U8;
					}
					else
					{
						es_fatal(ES_ERROR_CREATE_FILE);
					}
				}
				else		
				if (es_check_param(es_argv,L"csv"))
				{
					es_export = ES_EXPORT_CSV;
				}
				else				
				if (es_check_param(es_argv,L"tsv"))
				{
					es_export = ES_EXPORT_TSV;
				}
				else				
				if (es_check_param(es_argv,L"export-csv"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_export_file = CreateFile(es_argv,GENERIC_WRITE,FILE_SHARE_READ,0,CREATE_ALWAYS,0,0);
					if (es_export_file != INVALID_HANDLE_VALUE)
					{
						es_export = ES_EXPORT_CSV;
					}
					else
					{
						es_fatal(ES_ERROR_CREATE_FILE);
					}
				}
				else		
				if (es_check_param(es_argv,L"export-tsv"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_export_file = CreateFile(es_argv,GENERIC_WRITE,FILE_SHARE_READ,0,CREATE_ALWAYS,0,0);
					if (es_export_file != INVALID_HANDLE_VALUE)
					{
						es_export = ES_EXPORT_TSV;
					}
					else
					{
						es_fatal(ES_ERROR_CREATE_FILE);
					}
				}
				else		
				if (es_check_param(es_argv,L"efu"))
				{
					es_export = ES_EXPORT_EFU;
				}
				else		
				if (es_check_param(es_argv,L"export-efu"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_export_file = CreateFile(es_argv,GENERIC_WRITE,FILE_SHARE_READ,0,CREATE_ALWAYS,0,0);
					if (es_export_file != INVALID_HANDLE_VALUE)
					{
						es_export = ES_EXPORT_EFU;
					}
					else
					{
						es_fatal(ES_ERROR_CREATE_FILE);
					}
				}
				else		
				if (es_check_param(es_argv,L"txt"))
				{
					es_export = ES_EXPORT_TXT;
				}
				else				
				if (es_check_param(es_argv,L"export-txt"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_export_file = CreateFile(es_argv,GENERIC_WRITE,FILE_SHARE_READ,0,CREATE_ALWAYS,0,0);
					if (es_export_file != INVALID_HANDLE_VALUE)
					{
						es_export = ES_EXPORT_TXT;
					}
					else
					{
						es_fatal(ES_ERROR_CREATE_FILE);
					}
				}
				else
				if ((es_check_param(es_argv,L"w")) || (es_check_param(es_argv,L"ww")) || (es_check_param(es_argv,L"whole-word")) || (es_check_param(es_argv,L"whole-words")))
				{
					es_match_whole_word = 1;
				}
				else
				if ((es_check_param(es_argv,L"no-w")) || (es_check_param(es_argv,L"no-ww")) || (es_check_param(es_argv,L"no-whole-word")) || (es_check_param(es_argv,L"no-whole-words")))
				{
					es_match_whole_word = 0;
				}
				else
				if ((es_check_param(es_argv,L"p")) || (es_check_param(es_argv,L"match-path")))
				{
					es_match_path = 1;
				}
				else
				if (es_check_param(es_argv,L"no-p"))
				{
					es_match_path = 0;
				}
				else
				if ((es_check_param(es_argv,L"file-name-width")) || (es_check_param(es_argv,L"file-name-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_FILENAME);
					es_column_widths[ES_COLUMN_HIGHLIGHTED_FILENAME] = es_column_widths[ES_COLUMN_FILENAME];
				}
				else
				if ((es_check_param(es_argv,L"name-width")) || (es_check_param(es_argv,L"name-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_NAME);
					es_column_widths[ES_COLUMN_HIGHLIGHTED_NAME] = es_column_widths[ES_COLUMN_NAME];
				}
				else
				if ((es_check_param(es_argv,L"path-width")) || (es_check_param(es_argv,L"path-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_PATH);
					es_column_widths[ES_COLUMN_HIGHLIGHTED_PATH] = es_column_widths[ES_COLUMN_PATH];
				}
				else
				if ((es_check_param(es_argv,L"extension-width")) || (es_check_param(es_argv,L"extension-wide")) || (es_check_param(es_argv,L"ext-width")) || (es_check_param(es_argv,L"ext-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_EXTENSION);
				}
				else
				if ((es_check_param(es_argv,L"size-width")) || (es_check_param(es_argv,L"size-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_SIZE);
				}
				else
				if ((es_check_param(es_argv,L"date-created-width")) || (es_check_param(es_argv,L"date-created-wide")) || (es_check_param(es_argv,L"dc-width")) || (es_check_param(es_argv,L"dc-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_DATE_CREATED);
				}
				else
				if ((es_check_param(es_argv,L"date-modified-width")) || (es_check_param(es_argv,L"date-modified-wide")) || (es_check_param(es_argv,L"dm-width")) || (es_check_param(es_argv,L"dm-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_DATE_MODIFIED);
				}
				else
				if ((es_check_param(es_argv,L"date-accessed-width")) || (es_check_param(es_argv,L"date-accessed-wide")) || (es_check_param(es_argv,L"da-width")) || (es_check_param(es_argv,L"da-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_DATE_ACCESSED);
				}
				else
				if ((es_check_param(es_argv,L"attributes-width")) || (es_check_param(es_argv,L"attributes-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_ATTRIBUTES);
				}
				else
				if ((es_check_param(es_argv,L"file-list-file-name-width")) || (es_check_param(es_argv,L"file-list-file-name-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_FILE_LIST_FILENAME);
				}
				else
				if ((es_check_param(es_argv,L"run-count-width")) || (es_check_param(es_argv,L"run-count-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_RUN_COUNT);
				}
				else
				if ((es_check_param(es_argv,L"date-run-width")) || (es_check_param(es_argv,L"date-run-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_DATE_RUN);
				}
				else
				if ((es_check_param(es_argv,L"date-recently-changed-width")) || (es_check_param(es_argv,L"date-recently-changed-wide")) || (es_check_param(es_argv,L"rc-width")) || (es_check_param(es_argv,L"rc-wide")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_column_wide(ES_COLUMN_DATE_RECENTLY_CHANGED);
				}
				else
				if (es_check_param(es_argv,L"size-leading-zero"))
				{
					es_size_leading_zero = 1;
				}
				else
				if (es_check_param(es_argv,L"no-size-leading-zero"))
				{
					es_size_leading_zero = 0;
				}
				else
				if (es_check_param(es_argv,L"run-count-leading-zero"))
				{
					es_run_count_leading_zero = 1;
				}
				else
				if (es_check_param(es_argv,L"no-run-count-leading-zero"))
				{
					es_run_count_leading_zero = 0;
				}
				else
				if (es_check_param(es_argv,L"no-digit-grouping"))
				{
					es_digit_grouping = 0;
				}
				else
				if (es_check_param(es_argv,L"digit-grouping"))
				{
					es_digit_grouping = 1;
				}
				else
				if (es_check_param(es_argv,L"size-format"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_size_format = es_wstring_to_int(es_argv);
				}
				else
				if (es_check_param(es_argv,L"date-format"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_date_format = es_wstring_to_int(es_argv);
				}
				else			
				if ((es_check_param(es_argv,L"pause")) || (es_check_param(es_argv,L"more")))
				{
					es_pause = 1;
				}
				else
				if ((es_check_param(es_argv,L"no-pause")) || (es_check_param(es_argv,L"no-more")))
				{
					es_pause = 0;
				}
				else
				if (es_check_param(es_argv,L"empty-search-help"))
				{
					es_empty_search_help = 1;
					es_hide_empty_search_results = 0;
				}
				else
				if (es_check_param(es_argv,L"no-empty-search-help"))
				{
					es_empty_search_help = 0;
				}
				else
				if (es_check_param(es_argv,L"hide-empty-search-results"))
				{
					es_hide_empty_search_results = 1;
					es_empty_search_help = 0;
				}
				else
				if (es_check_param(es_argv,L"no-hide-empty-search-results"))
				{
					es_hide_empty_search_results = 0;
				}
				else
				if (es_check_param(es_argv,L"save-settings"))
				{
					es_save = 1;
				}
				else
				if (es_check_param(es_argv,L"clear-settings"))
				{
					char filename[MAX_PATH];
					
					if (es_get_ini_filename(filename))
					{
						DeleteFileA(filename);

						es_write(L"Settings saved.\r\n");
					}
					
					goto exit;
				}
				else
				if (es_check_param(es_argv,L"filename-color"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_set_color(ES_COLUMN_FILENAME);
					es_column_color[ES_COLUMN_HIGHLIGHTED_FILENAME] = es_column_color[ES_COLUMN_FILENAME];
					es_column_color_is_valid[ES_COLUMN_HIGHLIGHTED_FILENAME] = es_column_color_is_valid[ES_COLUMN_FILENAME];
				}
				else			
				if (es_check_param(es_argv,L"name-color"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
								
					es_set_color(ES_COLUMN_NAME);
					es_column_color[ES_COLUMN_NAME] = es_column_color[ES_COLUMN_NAME];
					es_column_color_is_valid[ES_COLUMN_NAME] = es_column_color_is_valid[ES_COLUMN_NAME];
				}
				else			
				if (es_check_param(es_argv,L"path-color"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
								
					es_set_color(ES_COLUMN_PATH);
					es_column_color[ES_COLUMN_PATH] = es_column_color[ES_COLUMN_PATH];
					es_column_color_is_valid[ES_COLUMN_PATH] = es_column_color_is_valid[ES_COLUMN_PATH];
				}
				else		
				if (es_check_param(es_argv,L"extension-color"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
								
					es_set_color(ES_COLUMN_EXTENSION);
				}
				else
				if (es_check_param(es_argv,L"size-color"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
								
					es_set_color(ES_COLUMN_SIZE);
				}
				else
				if ((es_check_param(es_argv,L"date-created-color")) || (es_check_param(es_argv,L"dc-color")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
								
					es_set_color(ES_COLUMN_DATE_CREATED);
				}
				else
				if ((es_check_param(es_argv,L"date-modified-color")) || (es_check_param(es_argv,L"dm-color")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
								
					es_set_color(ES_COLUMN_DATE_MODIFIED);
				}
				else
				if ((es_check_param(es_argv,L"date-accessed-color")) || (es_check_param(es_argv,L"da-color")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
								
					es_set_color(ES_COLUMN_DATE_ACCESSED);
				}
				else			
				if ((es_check_param(es_argv,L"attributes-color")) || (es_check_param(es_argv,L"attribs-color")) || (es_check_param(es_argv,L"attrib-color"))) 
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
								
					es_set_color(ES_COLUMN_ATTRIBUTES);
				}
				else			
				if (es_check_param(es_argv,L"file-list-filename-color"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
								
					es_set_color(ES_COLUMN_FILE_LIST_FILENAME);
				}
				else
				if (es_check_param(es_argv,L"run-count-color"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
								
					es_set_color(ES_COLUMN_RUN_COUNT);
				}
				else			
				if (es_check_param(es_argv,L"date-run-color"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
								
					es_set_color(ES_COLUMN_DATE_RUN);
				}
				else
				if ((es_check_param(es_argv,L"date-recently-changed-color")) || (es_check_param(es_argv,L"rc-color")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
								
					es_set_color(ES_COLUMN_DATE_RECENTLY_CHANGED);
				}
				else
				if (es_check_param(es_argv,L"name"))
				{
					es_add_column(ES_COLUMN_NAME);
				}
				else
				if (es_check_param(es_argv,L"no-name"))
				{
					es_remove_column(ES_COLUMN_NAME);
				}
				else
				if (es_check_param(es_argv,L"path-column"))
				{
					es_add_column(ES_COLUMN_PATH);
				}
				else
				if (es_check_param(es_argv,L"no-path-column"))
				{
					es_remove_column(ES_COLUMN_PATH);
				}
				else
				if ((es_check_param(es_argv,L"full-path-and-name")) || (es_check_param(es_argv,L"path-and-name")) || (es_check_param(es_argv,L"filename-column")))
				{
					es_add_column(ES_COLUMN_FILENAME);
				}
				else
				if ((es_check_param(es_argv,L"no-full-path-and-name")) || (es_check_param(es_argv,L"no-path-and-name")) || (es_check_param(es_argv,L"no-filename-column")))
				{
					es_remove_column(ES_COLUMN_FILENAME);
				}
				else
				if ((es_check_param(es_argv,L"extension")) || (es_check_param(es_argv,L"ext")))
				{
					es_add_column(ES_COLUMN_EXTENSION);
				}
				else
				if ((es_check_param(es_argv,L"no-extension")) || (es_check_param(es_argv,L"no-ext")))
				{
					es_remove_column(ES_COLUMN_EXTENSION);
				}
				else
				if (es_check_param(es_argv,L"size"))
				{
					es_add_column(ES_COLUMN_SIZE);
				}
				else
				if (es_check_param(es_argv,L"no-size"))
				{
					es_remove_column(ES_COLUMN_SIZE);
				}
				else
				if ((es_check_param(es_argv,L"date-created")) || (es_check_param(es_argv,L"dc")))
				{
					es_add_column(ES_COLUMN_DATE_CREATED);
				}
				else
				if ((es_check_param(es_argv,L"no-date-created")) || (es_check_param(es_argv,L"no-dc")))
				{
					es_remove_column(ES_COLUMN_DATE_CREATED);
				}
				else
				if ((es_check_param(es_argv,L"date-modified")) || (es_check_param(es_argv,L"dm")))
				{
					es_add_column(ES_COLUMN_DATE_MODIFIED);
				}
				else
				if ((es_check_param(es_argv,L"no-date-modified")) || (es_check_param(es_argv,L"no-dm")))
				{
					es_remove_column(ES_COLUMN_DATE_MODIFIED);
				}
				else
				if ((es_check_param(es_argv,L"date-accessed")) || (es_check_param(es_argv,L"da")))
				{
					es_add_column(ES_COLUMN_DATE_ACCESSED);
				}
				else
				if ((es_check_param(es_argv,L"no-date-accessed")) || (es_check_param(es_argv,L"no-da")))
				{
					es_remove_column(ES_COLUMN_DATE_ACCESSED);
				}
				else
				if ((es_check_param(es_argv,L"attributes")) || (es_check_param(es_argv,L"attribs")) || (es_check_param(es_argv,L"attrib")))
				{
					es_add_column(ES_COLUMN_ATTRIBUTES);
				}
				else
				if ((es_check_param(es_argv,L"no-attributes")) || (es_check_param(es_argv,L"no-attribs")) || (es_check_param(es_argv,L"no-attrib")))
				{
					es_remove_column(ES_COLUMN_ATTRIBUTES);
				}
				else
				if ((es_check_param(es_argv,L"file-list-file-name")) || (es_check_param(es_argv,L"flfn")))
				{
					es_add_column(ES_COLUMN_FILE_LIST_FILENAME);
				}
				else
				if ((es_check_param(es_argv,L"no-file-list-file-name")) || (es_check_param(es_argv,L"no-flfn")))
				{
					es_remove_column(ES_COLUMN_FILE_LIST_FILENAME);
				}
				else
				if (es_check_param(es_argv,L"run-count"))
				{
					es_add_column(ES_COLUMN_RUN_COUNT);
				}
				else
				if (es_check_param(es_argv,L"no-run-count"))
				{
					es_remove_column(ES_COLUMN_RUN_COUNT);
				}
				else
				if (es_check_param(es_argv,L"date-run"))
				{
					es_add_column(ES_COLUMN_DATE_RUN);
				}
				else
				if (es_check_param(es_argv,L"no-date-run"))
				{
					es_remove_column(ES_COLUMN_DATE_RUN);
				}
				else
				if ((es_check_param(es_argv,L"date-recently-changed")) || (es_check_param(es_argv,L"rc")) || (es_check_param(es_argv,L"drc")) || (es_check_param(es_argv,L"recent-change")))
				{
					es_add_column(ES_COLUMN_DATE_RECENTLY_CHANGED);
				}
				else
				if ((es_check_param(es_argv,L"no-date-recently-changed")) || (es_check_param(es_argv,L"no-rc")) || (es_check_param(es_argv,L"no-drc")) || (es_check_param(es_argv,L"no-recent-change")))
				{
					es_remove_column(ES_COLUMN_DATE_RECENTLY_CHANGED);
				}
				else
				if (es_check_param(es_argv,L"sort-ascending"))
				{
					es_sort_ascending = 1;
				}
				else
				if (es_check_param(es_argv,L"sort-descending"))
				{
					es_sort_ascending = -1;
				}
				else
				if (es_check_param(es_argv,L"sort"))
				{	
					wchar_t sortnamebuf[ES_BUF_SIZE];
					int sortnamei;
					
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					for(sortnamei=0;sortnamei<ES_SORT_NAME_COUNT;sortnamei++)
					{
						if (es_check_param_param(es_argv,es_sort_names[sortnamei]))
						{
							es_sort = es_sort_names_to_ids[sortnamei];

							break;
						}
						else
						{
							es_wstring_copy(sortnamebuf,es_sort_names[sortnamei]);
							es_wstring_cat(sortnamebuf,L"-ascending");
							
							if (es_check_param_param(es_argv,sortnamebuf))
							{
								es_sort = es_sort_names_to_ids[sortnamei];
								es_sort_ascending = 1;

								break;
							}
							else
							{
								es_wstring_copy(sortnamebuf,es_sort_names[sortnamei]);
								es_wstring_cat(sortnamebuf,L"-descending");
								
								if (es_check_param_param(es_argv,sortnamebuf))
								{
									es_sort = es_sort_names_to_ids[sortnamei];
									es_sort_ascending = -1;

									break;
								}
							}
						}
					}
					
					if (sortnamei == ES_SORT_NAME_COUNT)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
				}
				else
				if (es_check_param(es_argv,L"ipc1"))
				{	
					es_ipc_version = 0x01; 
				}
				else
				if (es_check_param(es_argv,L"ipc2"))
				{	
					es_ipc_version = 0x02; 
				}
				else
				if (es_check_param(es_argv,L"header"))
				{	
					es_header = 1; 
				}
				else
				if (es_check_param(es_argv,L"no-header"))
				{	
					es_header = 0; 
				}
				else
				if (es_check_param(es_argv,L"double-quote"))
				{	
					es_double_quote = 1; 
				}
				else
				if (es_check_param(es_argv,L"no-double-quote"))
				{	
					es_double_quote = 0; 
				}
				else
				if (es_check_param(es_argv,L"version"))
				{	
					es_write(ES_VERSION);
					es_write(L"\r\n");

					goto exit;
				}
				else
				if (es_check_param(es_argv,L"get-everything-version"))
				{
					es_get_everything_version = 1;
				}
				else
				if (es_check_param(es_argv,L"utf8-bom"))
				{
					es_utf8_bom = 1;
				}
				else
				if (es_check_sorts())
				{
				}
				else
				if (es_check_param(es_argv,L"on"))
				{	
					es_sort = EVERYTHING_IPC_SORT_NAME_ASCENDING;
					es_sort_ascending = 1;
				}
				else
				if (es_check_param(es_argv,L"o-n"))
				{	
					es_sort = EVERYTHING_IPC_SORT_NAME_DESCENDING;
					es_sort_ascending = -1;
				}
				else
				if (es_check_param(es_argv,L"os"))
				{	
					es_sort = EVERYTHING_IPC_SORT_SIZE_ASCENDING;
					es_sort_ascending = 1;
				}
				else
				if (es_check_param(es_argv,L"o-s"))
				{	
					es_sort = EVERYTHING_IPC_SORT_SIZE_DESCENDING;
					es_sort_ascending = -1;
				}
				else
				if (es_check_param(es_argv,L"oe"))
				{	
					es_sort = EVERYTHING_IPC_SORT_EXTENSION_ASCENDING;
					es_sort_ascending = 1;
				}
				else
				if (es_check_param(es_argv,L"o-e"))
				{	
					es_sort = EVERYTHING_IPC_SORT_EXTENSION_DESCENDING;
					es_sort_ascending = -1;
				}
				else
				if (es_check_param(es_argv,L"od"))
				{	
					es_sort = EVERYTHING_IPC_SORT_DATE_MODIFIED_ASCENDING;
					es_sort_ascending = 1;
				}
				else
				if (es_check_param(es_argv,L"o-d"))
				{	
					es_sort = EVERYTHING_IPC_SORT_DATE_MODIFIED_DESCENDING;
					es_sort_ascending = -1;
				}
				else
				if (es_check_param(es_argv,L"s"))
				{
					es_sort = EVERYTHING_IPC_SORT_PATH_ASCENDING;
				}
				else
				if ((es_check_param(es_argv,L"n")) || (es_check_param(es_argv,L"max-results")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_max_results = es_wstring_to_int(es_argv);
				}
				else
				if ((es_check_param(es_argv,L"o")) || (es_check_param(es_argv,L"offset")))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_offset = es_wstring_to_int(es_argv);
				}
				else
				if (es_check_param(es_argv,L"path"))
				{
					wchar_t pathbuf[ES_BUF_SIZE];
					wchar_t *namepart;
				
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					// relative path.
					GetFullPathName(es_argv,ES_BUF_SIZE,pathbuf,&namepart);
					
					if (*es_filter)
					{
						es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L" ");
					}

					es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L"\"");
					es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,pathbuf);
					if ((*pathbuf) && (pathbuf[es_wstring_length(pathbuf) - 1] != '\\'))
					{
						es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L"\\");
					}
					es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L"\"");
				}
				else
				if (es_check_param(es_argv,L"parent-path"))
				{
					wchar_t pathbuf[ES_BUF_SIZE];
					wchar_t *namepart;
				
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					// relative path.
					GetFullPathName(es_argv,ES_BUF_SIZE,pathbuf,&namepart);
					PathRemoveFileSpec(pathbuf);
					
					if (*es_filter)
					{
						es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L" ");
					}

					es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L"\"");
					es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,pathbuf);
					if ((*pathbuf) && (pathbuf[es_wstring_length(pathbuf) - 1] != '\\'))
					{
						es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L"\\");
					}
					es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L"\"");
				}
				else
				if (es_check_param(es_argv,L"parent"))
				{
					wchar_t pathbuf[ES_BUF_SIZE];
					wchar_t *namepart;
				
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					// relative path.
					GetFullPathName(es_argv,ES_BUF_SIZE,pathbuf,&namepart);
					
					if (*es_filter)
					{
						es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L" ");
					}

					es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L"parent:\"");
					es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,pathbuf);
					if ((*pathbuf) && (pathbuf[es_wstring_length(pathbuf) - 1] != '\\'))
					{
						es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L"\\");
					}
					es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L"\"");
				}
				else
				if (es_check_param(es_argv,L"time-out"))
				{
					command_line = es_get_command_argv(command_line);
					if (!command_line)
					{
						es_fatal(ES_ERROR_EXPECTED_SWITCH_PARAMETER);
					}
					
					es_timeout = (DWORD)es_wstring_to_int(es_argv);
				}
				else
				if (es_check_param(es_argv,L"no-time-out"))
				{
					es_timeout = 0;
				}
				else
				if (es_check_param(es_argv,L"ad"))
				{	
					// add folder:
					es_append_filter(L"folder:");
				}
				else
				if (es_check_param(es_argv,L"a-d"))
				{	
					// add folder:
					es_append_filter(L"file:");
				}
				else
				if (es_check_param(es_argv,L"get-result-count"))
				{
					es_get_result_count = 1;
				}
				else
				if (es_check_param(es_argv,L"get-total-size"))
				{
					es_get_total_size = 1;
				}
				else
				if (es_check_param(es_argv,L"no-result-error"))
				{
					es_no_result_error = 1;
				}
				else
				if (((es_argv[0] == '-') || (es_argv[0] == '/')) && (es_argv[1] == 'a') && (es_argv[2]))
				{
					const wchar_t *p;
					wchar_t attrib[ES_BUF_SIZE];
					wchar_t notattrib[ES_BUF_SIZE];
					wchar_t wch[2];

					p = es_argv + 2;
					
					attrib[0] = 0;
					notattrib[0] = 0;
					
					// TODO handle unknown a switches.
					while(*p)
					{
						int lower_p;
						
						lower_p = tolower(*p);
						
						if (lower_p == '-')
						{
							p++;
							
							lower_p = tolower(*p);
							
							wch[0] = lower_p;
							wch[1] = 0;
							
							es_wstring_cat(notattrib,wch);
						}
						else
						{
							wch[0] = lower_p;
							wch[1] = 0;
							
							es_wstring_cat(attrib,wch);
						}
												
						p++;						
					}
					
					// copy append to search
					if (*attrib)
					{
						if ((d != es_search) && (d < e)) *d++ = ' ';

						s = L"attrib:";
						while(*s)
						{
							if (d < e) *d++ = *s;
							s++;
						}
						
						// copy append to search
						s = attrib;
						while(*s)
						{
							if (d < e) *d++ = *s;
							s++;
						}
					}

					// copy not append to search
					if (*notattrib)
					{
						if ((d != es_search) && (d < e)) *d++ = ' ';
	
						s = L"!attrib:";
						while(*s)
						{
							if (d < e) *d++ = *s;
							s++;
						}
						
						// copy append to search
						s = notattrib;
						while(*s)
						{
							if (d < e) *d++ = *s;
							s++;
						}
					}
				}
				else
				if ((es_check_param(es_argv,L"?")) || (es_check_param(es_argv,L"help")) || (es_check_param(es_argv,L"h")))
				{
					// user requested help
					es_help();
					
					goto exit;
				}
				else
				if (es_argv[0] == '-')
				{
					// unknown command
					// allow /downloads to search for "\downloads" for now
					es_fatal(ES_ERROR_UNKNOWN_SWITCH);
				}
				else
				{
					if ((d != es_search) && (d < e)) *d++ = ' ';

					// copy append to search
					s = es_argv;
					while(*s)
					{
						if (d < e) *d++ = *s;
						s++;
					}
				}

				command_line = es_get_argv(command_line);
				if (!command_line)
				{
					break;
				}
			}
		}
	}
	
	// fix sort order
	if (es_sort_ascending)
	{
		if (es_sort_ascending > 0)
		{
			es_set_sort_ascending();
		}
		else
		{
			es_set_sort_descending();
		}
	}
		
	// save settings.
	if (es_save)
	{
		es_save_settings();
		
		es_write(L"Settings saved.\r\n");
		
		perform_search = 0;
	}
	
	// save settings.
	if (es_get_everything_version)
	{
		es_everything_hwnd = es_find_ipc_window();
		
		if (es_everything_hwnd)
		{
			int major;
			int minor;
			int revision;
			int build;

			// wait for DB_IS_LOADED so we don't get 0 results
			major = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_GET_MAJOR_VERSION,0);
			minor = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_GET_MINOR_VERSION,0);
			revision = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_GET_REVISION,0);
			build = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_GET_BUILD_NUMBER,0);
				
			es_write_DWORD(major);
			es_write(L".");
			es_write_DWORD(minor);
			es_write(L".");
			es_write_DWORD(revision);
			es_write(L".");
			es_write_DWORD(build);
			es_write(L"\r\n");
		}
		else
		{
			es_fatal(ES_ERROR_IPC);
		}
		
		perform_search = 0;
	}
	
	// reindex?
	if (es_reindex)
	{
		es_everything_hwnd = es_find_ipc_window();
		
		if (es_everything_hwnd)
		{
			SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_REBUILD_DB,0);
			
			// poll until db is available.
			es_wait_for_db_loaded();
		}
		else
		{
			es_fatal(ES_ERROR_IPC);
		}

		perform_search = 0;
	}
	
	// run history command
	if (es_run_history_command)
	{
		es_do_run_history_command();

		perform_search = 0;
	}
	
	// save db
	// do this after a reindex.
	if (es_save_db)
	{
		es_everything_hwnd = es_find_ipc_window();
		
		if (es_everything_hwnd)
		{
			SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_SAVE_DB,0);
			
			// wait until not busy..
			es_wait_for_db_not_busy();
		}
		else
		{
			es_fatal(ES_ERROR_IPC);
		}

		perform_search = 0;
	}
	
	// Exit Everything?
	if (es_exit_everything)
	{
		es_everything_hwnd = es_find_ipc_window();
		
		if (es_everything_hwnd)
		{
			DWORD dwProcessId;

			SendMessage(es_everything_hwnd,WM_CLOSE,0,0);

			// wait for Everything to exit.
			if (GetWindowThreadProcessId(es_everything_hwnd,&dwProcessId))
			{
				HANDLE h;
				
				h = OpenProcess(SYNCHRONIZE,FALSE,dwProcessId);
				if (h)
				{
					WaitForSingleObject(h,es_timeout ? es_timeout : INFINITE);
				
					CloseHandle(h);
				}
				else
				{
					es_fatal(ES_ERROR_SEND_MESSAGE);
				}
			}
			else
			{
				es_fatal(ES_ERROR_SEND_MESSAGE);
			}
		}
		else
		{
			es_fatal(ES_ERROR_IPC);
		}
		
		perform_search = 0;
	}
	
	if (perform_search)
	{
		// empty search?
		// if max results is set, treat the search as non-empty.
		// -useful if you want to see the top ten largest files etc..
		if ((d == es_search) && (!*es_filter) && (es_max_results == 0xffffffff) && (!es_get_result_count) && (!es_get_total_size))
		{
			if (es_empty_search_help)
			{
				es_help();
				
				goto exit;
			}

			if (es_hide_empty_search_results)
			{
				goto exit;
			}
		}
		
		// add filename column
		if (es_find_column(ES_COLUMN_FILENAME) == -1)
		{
			if (es_find_column(ES_COLUMN_NAME) == -1)
			{
				if (es_find_column(ES_COLUMN_PATH) == -1)
				{
					es_add_column(ES_COLUMN_FILENAME);
				}
			}
		}
		
		// apply highlighting to columns.
		if (es_highlight)
		{
			int columni;
			
			for(columni=0;columni<es_numcolumns;columni++)
			{
				if (es_columns[columni] == ES_COLUMN_FILENAME)
				{
					es_columns[columni] = ES_COLUMN_HIGHLIGHTED_FILENAME;
				}
				else
				if (es_columns[columni] == ES_COLUMN_PATH)
				{
					es_columns[columni] = ES_COLUMN_HIGHLIGHTED_PATH;
				}
				else
				if (es_columns[columni] == ES_COLUMN_NAME)
				{
					es_columns[columni] = ES_COLUMN_HIGHLIGHTED_NAME;
				}
			}
		}
		
		// null terminate
		*d = 0;

		// write export headers
		// write header
		if (es_export)
		{
			if (es_utf8_bom)
			{
				if (es_export_file != INVALID_HANDLE_VALUE)
				{
					BYTE bom[3];
					DWORD numwritten;
					
					// 0xEF,0xBB,0xBF.
					bom[0] = 0xEF;
					bom[1] = 0xBB;
					bom[2] = 0xBF;
					
					WriteFile(es_export_file,bom,3,&numwritten,0);
				}
			}
		
			// disable pause
			es_pause = 0;
		
			// remove highlighting.
			{
				int columni;
				
				for(columni=0;columni<es_numcolumns;columni++)
				{
					if (es_columns[columni] == ES_COLUMN_HIGHLIGHTED_FILENAME)
					{
						es_columns[columni] = ES_COLUMN_FILENAME;
					}
					else
					if (es_columns[columni] == ES_COLUMN_HIGHLIGHTED_PATH)
					{
						es_columns[columni] = ES_COLUMN_PATH;
					}
					else
					if (es_columns[columni] == ES_COLUMN_HIGHLIGHTED_NAME)
					{
						es_columns[columni] = ES_COLUMN_NAME;
					}
				}
			}
			
			if (es_export_file != INVALID_HANDLE_VALUE)
			{
				es_export_buf = es_alloc(ES_EXPORT_BUF_SIZE);
				es_export_p = es_export_buf;
				es_export_remaining = ES_EXPORT_BUF_SIZE;
			}
			
			if ((es_export == ES_EXPORT_CSV) || (es_export == ES_EXPORT_TSV))
			{
				if (es_header)
				{
					int columni;
					
					for(columni=0;columni<es_numcolumns;columni++)
					{
						if (columni)
						{
							es_fwrite((es_export == ES_EXPORT_CSV) ? L"," : L"\t");
						}

						es_fwrite(es_column_names[es_columns[columni]]);
					}

					es_fwrite(L"\r\n");
				}
			}
			else
			if (es_export == ES_EXPORT_EFU)
			{
				int was_size_column;
				int was_date_modified_column;
				int was_date_created_column;
				int was_attributes_column;
				int column_i;

				was_size_column = 0;
				was_date_modified_column = 0;
				was_date_created_column = 0;
				was_attributes_column = 0;
							
				for(column_i=0;column_i<es_numcolumns;column_i++)
				{
					switch(es_columns[column_i])
					{
						case ES_COLUMN_SIZE:
							was_size_column = 1;
							break;
							
						case ES_COLUMN_DATE_MODIFIED:
							was_date_modified_column = 1;
							break;
							
						case ES_COLUMN_DATE_CREATED:
							was_date_created_column = 1;
							break;
							
						case ES_COLUMN_ATTRIBUTES:
							was_attributes_column = 1;
							break;
					}
				}
				
				// reset columns and force Filename,Size,Date Modified,Date Created,Attributes.
				es_numcolumns = 0;
				es_add_column(ES_COLUMN_FILENAME);
				
				if (was_size_column)
				{
					es_add_column(ES_COLUMN_SIZE);
				}

				if (was_date_modified_column)
				{
					es_add_column(ES_COLUMN_DATE_MODIFIED);
				}

				if (was_date_created_column)
				{
					es_add_column(ES_COLUMN_DATE_CREATED);
				}

				if (was_attributes_column)
				{
					es_add_column(ES_COLUMN_ATTRIBUTES);
				}

				if (es_header)
				{
					es_fwrite(L"Filename,Size,Date Modified,Date Created,Attributes\r\n");
				}
			}
			else
			if ((es_export == ES_EXPORT_TXT) || (es_export == ES_EXPORT_M3U) || (es_export == ES_EXPORT_M3U8))
			{
				// reset columns and force Filename.
				es_numcolumns = 0;
				es_add_column(ES_COLUMN_FILENAME);
			}
		}

		// fix search filter
		if (*es_filter)
		{
			wchar_t *new_search;
			
			new_search = es_alloc(ES_SEARCH_BUF_SIZE * sizeof(wchar_t));
			
			*new_search = 0;
			
			es_wbuf_cat(new_search,ES_SEARCH_BUF_SIZE,L"<");
			es_wbuf_cat(new_search,ES_SEARCH_BUF_SIZE,es_filter);
			es_wbuf_cat(new_search,ES_SEARCH_BUF_SIZE,L"><");
			es_wbuf_cat(new_search,ES_SEARCH_BUF_SIZE,es_search);
			es_wbuf_cat(new_search,ES_SEARCH_BUF_SIZE,L">");
			
			es_free(es_search);
			
			es_search = new_search;
		}

		{
			int got_indexed_file_info;
			
			got_indexed_file_info = 0;
			
			es_everything_hwnd = es_find_ipc_window();
			if (es_everything_hwnd)
			{
				if (!got_indexed_file_info)
				{
					if (es_export == ES_EXPORT_EFU)
					{
						// get indexed file info column for exporting.
						if (SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_IS_FILE_INFO_INDEXED,EVERYTHING_IPC_FILE_INFO_FILE_SIZE))
						{
							es_add_column(ES_COLUMN_SIZE);
						}
						
						if (SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_IS_FILE_INFO_INDEXED,EVERYTHING_IPC_FILE_INFO_DATE_MODIFIED))
						{
							es_add_column(ES_COLUMN_DATE_MODIFIED);
						}
						
						if (SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_IS_FILE_INFO_INDEXED,EVERYTHING_IPC_FILE_INFO_DATE_CREATED))
						{
							es_add_column(ES_COLUMN_DATE_CREATED);
						}
						
						if (SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_IS_FILE_INFO_INDEXED,EVERYTHING_IPC_FILE_INFO_ATTRIBUTES))
						{
							es_add_column(ES_COLUMN_ATTRIBUTES);
						}
					}
					
					got_indexed_file_info = 1;
				}
		
				if (es_ipc_version & 2)
				{
					if (es_sendquery2(hwnd)) 
					{
						// success
						// don't try version 1.
						goto query_sent;
					}
				}

				if (es_ipc_version & 1)
				{
					if (es_sendquery(hwnd)) 
					{
						// success
						// don't try other versions.
						goto query_sent;
					}
				}

				es_fatal(ES_ERROR_SEND_MESSAGE);
			}
		}

query_sent:


		// message pump
	loop:

		// update windows
		if (PeekMessage(&msg,NULL,0,0,0)) 
		{
			ret = (int)GetMessage(&msg,0,0,0);
			if (ret <= 0) goto exit;

			// let windows handle it.
			TranslateMessage(&msg);
			DispatchMessage(&msg);
		}			
		else
		{
			WaitMessage();
		}
		
		goto loop;
	}

exit:

	if (es_run_history_data)
	{
		es_free(es_run_history_data);
	}

	if (es_argv)
	{
		es_free(es_argv);
	}

	if (es_search)
	{
		es_free(es_search);
	}
	
	if (es_filter)
	{
		es_free(es_filter);
	}
	
	if (es_instance)
	{
		es_free(es_instance);
	}
	
	if (es_connect)
	{
		es_free(es_connect);
	}

	es_flush();
	
	if (es_export_buf)
	{
		es_free(es_export_buf);
	}
	
	if (es_export_file != INVALID_HANDLE_VALUE)
	{
		CloseHandle(es_export_file);
	}
	
	if (es_user32_hdll)
	{
		FreeLibrary(es_user32_hdll);
	}
		
	if (es_ret != ES_ERROR_SUCCESS)
	{
		es_fatal(es_ret);
	}
		
	return ES_ERROR_SUCCESS;
}

int es_wstring_to_int(const wchar_t *s)
{
	const wchar_t *p;
	int value;
	
	p = s;
	value = 0;
	
	if ((*p == '0') && ((p[1] == 'x') || (p[1] == 'X')))
	{
		p += 2;
		
		while(*p)
		{
			if ((*p >= '0') && (*p <= '9'))
			{
				value *= 16;
				value += *p - '0';
			}
			else
			if ((*p >= 'A') && (*p <= 'F'))
			{
				value *= 16;
				value += *p - 'A' + 10;
			}
			else
			if ((*p >= 'a') && (*p <= 'f'))
			{
				value *= 16;
				value += *p - 'a' + 10;
			}
			else
			{
				break;
			}
			
			p++;
		}
	}
	else
	{
		while(*p)
		{
			if (!((*p >= '0') && (*p <= '9')))
			{
				break;
			}
			
			value *= 10;
			value += *p - '0';
			p++;
		}
	}
	
	return value;
}

// find the Everything IPC window
HWND es_find_ipc_window(void)
{
	DWORD tickstart;
	DWORD tick;
	wchar_t window_class[ES_BUF_SIZE];
	HWND ret;

	tickstart = GetTickCount();
	
	*window_class = 0;
	
	es_wstring_cat(window_class,EVERYTHING_IPC_WNDCLASS);
	
	if (*es_instance)
	{
		es_wstring_cat(window_class,L"_(");
		es_wstring_cat(window_class,es_instance);
		es_wstring_cat(window_class,L")");
	}

	ret = 0;
	
	for(;;)
	{
		HWND hwnd;
		
		hwnd = FindWindow(window_class,0);

		if (hwnd)
		{
			// wait for DB_IS_LOADED so we don't get 0 results
			if (es_timeout)
			{
				int major;
				int minor;
				int is_db_loaded;
				
				major = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_GET_MAJOR_VERSION,0);
				minor = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_GET_MINOR_VERSION,0);
				
				if (((major == 1) && (minor >= 4)) || (major > 1))
				{
					is_db_loaded = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_IS_DB_LOADED,0);
					
					if (!is_db_loaded)
					{
						goto wait;
					}
				}
			}
			
			ret = hwnd;
			break;
		}

wait:
		
		if (!es_timeout)
		{
			// the everything window was not found.
			// we can optionally RegisterWindowMessage("EVERYTHING_IPC_CREATED") and 
			// wait for Everything to post this message to all top level windows when its up and running.
			es_fatal(ES_ERROR_IPC);
		}
		
		// try again..
		Sleep(10);
		
		tick = GetTickCount();
		
		if (tick - tickstart > es_timeout)
		{
			// the everything window was not found.
			// we can optionally RegisterWindowMessage("EVERYTHING_IPC_CREATED") and 
			// wait for Everything to post this message to all top level windows when its up and running.
			es_fatal(ES_ERROR_IPC);
		}
	}
	
	return ret;
}

// find the Everything IPC window
static void es_wait_for_db_loaded(void)
{
	DWORD tickstart;
	wchar_t window_class[ES_BUF_SIZE];

	tickstart = GetTickCount();
	
	*window_class = 0;
	
	es_wstring_cat(window_class,EVERYTHING_IPC_WNDCLASS);
	
	if (*es_instance)
	{
		es_wstring_cat(window_class,L"_(");
		es_wstring_cat(window_class,es_instance);
		es_wstring_cat(window_class,L")");
	}

	for(;;)
	{
		HWND hwnd;
		DWORD tick;
		
		hwnd = FindWindow(window_class,0);

		if (hwnd)
		{
			int major;
			int minor;
			int is_db_loaded;
			
			// wait for DB_IS_LOADED so we don't get 0 results
			major = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_GET_MAJOR_VERSION,0);
			minor = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_GET_MINOR_VERSION,0);
			
			if (((major == 1) && (minor >= 4)) || (major > 1))
			{
				is_db_loaded = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_IS_DB_LOADED,0);
				
				if (is_db_loaded)
				{
					break;
				}
			}
			else
			{
				// can't wait
				break;
			}
		}
		else
		{
			// window was closed.
			break;
		}
		
		// try again..
		Sleep(10);
		
		tick = GetTickCount();
		
		if (es_timeout)
		{
			if (tick - tickstart > es_timeout)
			{
				// the everything window was not found.
				// we can optionally RegisterWindowMessage("EVERYTHING_IPC_CREATED") and 
				// wait for Everything to post this message to all top level windows when its up and running.
				es_fatal(ES_ERROR_IPC);
			}
		}
	}
}

// find the Everything IPC window
static void es_wait_for_db_not_busy(void)
{
	DWORD tickstart;
	wchar_t window_class[ES_BUF_SIZE];

	tickstart = GetTickCount();
	
	*window_class = 0;
	
	es_wstring_cat(window_class,EVERYTHING_IPC_WNDCLASS);
	
	if (*es_instance)
	{
		es_wstring_cat(window_class,L"_(");
		es_wstring_cat(window_class,es_instance);
		es_wstring_cat(window_class,L")");
	}

	for(;;)
	{
		HWND hwnd;
		DWORD tick;
		
		hwnd = FindWindow(window_class,0);

		if (hwnd)
		{
			int major;
			int minor;
			
			// wait for DB_IS_LOADED so we don't get 0 results
			major = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_GET_MAJOR_VERSION,0);
			minor = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_GET_MINOR_VERSION,0);
			
			if (((major == 1) && (minor >= 4)) || (major > 1))
			{
				int is_busy;
				
				is_busy = (int)SendMessage(es_everything_hwnd,EVERYTHING_WM_IPC,EVERYTHING_IPC_IS_DB_BUSY,0);
				
				if (!is_busy)
				{
					break;
				}
			}
			else
			{
				// can't check.
				break;
			}
		}
		else
		{
			// window was closed.
			break;
		}
		
		// try again..
		Sleep(10);
		
		tick = GetTickCount();
		
		if (es_timeout)
		{
			if (tick - tickstart > es_timeout)
			{
				// the everything window was not found.
				// we can optionally RegisterWindowMessage("EVERYTHING_IPC_CREATED") and 
				// wait for Everything to post this message to all top level windows when its up and running.
				es_fatal(ES_ERROR_IPC);
			}
		}
	}
}

void es_wstring_copy(wchar_t *buf,const wchar_t *s)
{
	int max;
	
	max = ES_BUF_SIZE - 1;
	while(max)
	{
		if (!*s) 
		{
			break;
		}
		
		*buf = *s;
		
		buf++;
		s++;
		max--;
	}
	
	*buf = 0;
}

// cat a string to buf
// max MUST be > 0
void es_wstring_cat(wchar_t *buf,const wchar_t *s)
{
	es_wbuf_cat(buf,ES_BUF_SIZE,s);
}

// cat a string to buf
// max MUST be > 0
void es_wstring_cat_qword(wchar_t *buf,QWORD qw)
{
	wchar_t qwbuf[ES_BUF_SIZE];
	
	es_wstring_print_qword(qwbuf,qw);
	
	es_wstring_cat(buf,qwbuf);
	
}

int es_check_param(wchar_t *param,const wchar_t *s)
{
	if ((*param == '-') || (*param == '/'))
	{
		param++;
		
		return es_check_param_param(param,s);
	}
	
	return 0;
}

int es_check_param_param(wchar_t *param,const wchar_t *s)
{
	while(*s)
	{
		if (*s == '-')
		{
			if (*param == '-')
			{
				param++;
			}
		
			s++;
		}
		else
		{
			if (*param != *s)
			{
				return 0;
			}
		
			param++;
			s++;
		}
	}
	
	if (*param)
	{
		return 0;
	}
	
	return 1;
}

void es_set_sort_ascending(void)
{
	switch(es_sort)
	{
		case EVERYTHING_IPC_SORT_NAME_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_NAME_ASCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_PATH_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_PATH_ASCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_SIZE_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_SIZE_ASCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_EXTENSION_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_EXTENSION_ASCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_TYPE_NAME_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_TYPE_NAME_ASCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_DATE_CREATED_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_DATE_CREATED_ASCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_DATE_MODIFIED_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_DATE_MODIFIED_ASCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_ATTRIBUTES_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_ATTRIBUTES_ASCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_FILE_LIST_FILENAME_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_FILE_LIST_FILENAME_ASCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_RUN_COUNT_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_RUN_COUNT_ASCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_DATE_RECENTLY_CHANGED_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_DATE_RECENTLY_CHANGED_ASCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_DATE_ACCESSED_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_DATE_ACCESSED_ASCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_DATE_RUN_DESCENDING:
			es_sort = EVERYTHING_IPC_SORT_DATE_RUN_ASCENDING;
			break;
	}
}
	
void es_set_sort_descending(void)
{
	switch(es_sort)
	{
		case EVERYTHING_IPC_SORT_NAME_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_NAME_DESCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_PATH_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_PATH_DESCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_SIZE_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_SIZE_DESCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_EXTENSION_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_EXTENSION_DESCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_TYPE_NAME_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_TYPE_NAME_DESCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_DATE_CREATED_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_DATE_CREATED_DESCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_DATE_MODIFIED_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_DATE_MODIFIED_DESCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_ATTRIBUTES_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_ATTRIBUTES_DESCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_FILE_LIST_FILENAME_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_FILE_LIST_FILENAME_DESCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_RUN_COUNT_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_RUN_COUNT_DESCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_DATE_RECENTLY_CHANGED_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_DATE_RECENTLY_CHANGED_DESCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_DATE_ACCESSED_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_DATE_ACCESSED_DESCENDING;
			break;
			
		case EVERYTHING_IPC_SORT_DATE_RUN_ASCENDING:
			es_sort = EVERYTHING_IPC_SORT_DATE_RUN_DESCENDING;
			break;
	}
}

int es_find_column(int type)
{
	int i;
	
	for(i=0;i<es_numcolumns;i++)
	{
		if (es_columns[i] == type)
		{
			return i;
		}
	}

	return -1;
}
		
void es_add_column(int type)
{
	es_remove_column(type);
	
	es_columns[es_numcolumns++] = type;
}

void es_remove_column(type)
{
	int i;
	int newcount;
	
	newcount = 0;
	
	for(i=0;i<es_numcolumns;i++)
	{
		if (es_columns[i] != type)
		{
			es_columns[newcount++] = es_columns[i];
		}
	}
	
	es_numcolumns = newcount;
}
		
void *es_get_column_data(EVERYTHING_IPC_LIST2 *list,int index,int type)
{	
	char *p;
	EVERYTHING_IPC_ITEM2 *items;
	
	items = (EVERYTHING_IPC_ITEM2 *)(list + 1);
	
	p = ((char *)list) + items[index].data_offset;

	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_NAME)
	{
		DWORD len;

		if (type == ES_COLUMN_NAME)	
		{
			return p;
		}
		
		len = *(DWORD *)p;
		p += sizeof(DWORD);
		
		p += (len + 1) * sizeof(wchar_t);
	}		
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_PATH)
	{
		DWORD len;
		
		if (type == ES_COLUMN_PATH)	
		{
			return p;
		}
		
		len = *(DWORD *)p;
		p += sizeof(DWORD);
		
		p += (len + 1) * sizeof(wchar_t);
	}
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_FULL_PATH_AND_NAME)
	{
		DWORD len;
		
		if (type == ES_COLUMN_FILENAME)	
		{
			return p;
		}
		
		len = *(DWORD *)p;
		p += sizeof(DWORD);

		p += (len + 1) * sizeof(wchar_t);
	}
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_EXTENSION)
	{
		DWORD len;
		
		if (type == ES_COLUMN_EXTENSION)	
		{
			return p;
		}
		
		len = *(DWORD *)p;
		p += sizeof(DWORD);
		
		p += (len + 1) * sizeof(wchar_t);
	}
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_SIZE)
	{
		if (type == ES_COLUMN_SIZE)	
		{
			return p;
		}
		
		p += sizeof(LARGE_INTEGER);
	}
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_DATE_CREATED)
	{
		if (type == ES_COLUMN_DATE_CREATED)	
		{
			return p;
		}
		
		p += sizeof(FILETIME);
	}
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_DATE_MODIFIED)
	{
		if (type == ES_COLUMN_DATE_MODIFIED)	
		{
			return p;
		}
		
		p += sizeof(FILETIME);
	}
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_DATE_ACCESSED)
	{
		if (type == ES_COLUMN_DATE_ACCESSED)	
		{
			return p;
		}
		
		p += sizeof(FILETIME);
	}
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_ATTRIBUTES)
	{
		if (type == ES_COLUMN_ATTRIBUTES)	
		{
			return p;
		}
		
		p += sizeof(DWORD);
	}
		
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_FILE_LIST_FILE_NAME)
	{
		DWORD len;
		
		if (type == ES_COLUMN_FILE_LIST_FILENAME)	
		{
			return p;
		}
		
		len = *(DWORD *)p;
		p += sizeof(DWORD);
		
		p += (len + 1) * sizeof(wchar_t);
	}	
		
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_RUN_COUNT)
	{
		if (type == ES_COLUMN_RUN_COUNT)	
		{
			return p;
		}
		
		p += sizeof(DWORD);
	}	
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_DATE_RUN)
	{
		if (type == ES_COLUMN_DATE_RUN)	
		{
			return p;
		}
		
		p += sizeof(FILETIME);
	}		
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_DATE_RECENTLY_CHANGED)
	{
		if (type == ES_COLUMN_DATE_RECENTLY_CHANGED)	
		{
			return p;
		}
		
		p += sizeof(FILETIME);
	}	
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_HIGHLIGHTED_NAME)
	{
		DWORD len;
		
		if (type == ES_COLUMN_HIGHLIGHTED_NAME)	
		{
			return p;
		}
		
		len = *(DWORD *)p;
		p += sizeof(DWORD);
		
		p += (len + 1) * sizeof(wchar_t);
	}		
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_HIGHLIGHTED_PATH)
	{
		DWORD len;
		
		if (type == ES_COLUMN_HIGHLIGHTED_PATH)	
		{
			return p;
		}
		
		len = *(DWORD *)p;
		p += sizeof(DWORD);
		
		p += (len + 1) * sizeof(wchar_t);
	}
	
	if (list->request_flags & EVERYTHING_IPC_QUERY2_REQUEST_HIGHLIGHTED_FULL_PATH_AND_NAME)
	{
		DWORD len;
		
		if (type == ES_COLUMN_HIGHLIGHTED_FILENAME)	
		{
			return p;
		}
		
		len = *(DWORD *)p;
		p += sizeof(DWORD);
		
		p += (len + 1) * sizeof(wchar_t);
	}			
	
	return 0;
}

void es_format_dir(wchar_t *buf)
{
	es_wstring_copy(buf,L"<DIR>");
	
//	es_space_to_width(buf,es_size_width);
}

void es_format_size(wchar_t *buf,QWORD size)
{
	if (size != 0xffffffffffffffffI64)
	{
		if (es_size_format == 0)
		{
			// auto size.
			if (size < 1000)
			{
				es_wstring_print_qword(buf,size);
				es_wstring_cat(buf,L"  B");
			}
			else
			{
				const wchar_t *suffix;
				
				// get suffix
				if (size / 1024I64 < 1000)
				{
					size = ((size * 100) ) / 1024;
					
					suffix = L" KB";
				}
				else
				if (size / (1024I64*1024I64) < 1000)
				{
					size = ((size * 100) ) / 1048576;
					
					suffix = L" MB";
				}
				else
				if (size / (1024I64*1024I64*1024I64) < 1000)
				{
					size = ((size * 100) ) / (1024I64*1024I64*1024I64);
					
					suffix = L" GB";
				}
				else
				if (size / (1024I64*1024I64*1024I64*1024I64) < 1000)
				{
					size = ((size * 100) ) / (1024I64*1024I64*1024I64*1024I64);
					
					suffix = L" TB";
				}
				else
				{
					size = ((size * 100) ) / (1024I64*1024I64*1024I64*1024I64*1024I64);
					
					suffix = L" PB";
				}
				
				*buf = 0;
				
				if (size == 0)
				{
					es_wstring_cat_qword(buf,size);
					es_wstring_cat(buf,suffix);
				}
				else
				if (size < 10)
				{
					// 0.0x
					es_wstring_cat(buf,L"0.0");
					es_wstring_cat_qword(buf,size);
					es_wstring_cat(buf,suffix);
				}
				else
				if (size < 100)
				{
					// 0.xx
					es_wstring_cat(buf,L"0.");
					es_wstring_cat_qword(buf,size);
					es_wstring_cat(buf,suffix);
				}
				else
				if (size < 1000)
				{
					// x.xx
					es_wstring_cat_qword(buf,size/100);
					es_wstring_cat(buf,L".");
					if (size%100 < 10)
					{
						// leading zero
						es_wstring_cat_qword(buf,0);
					}
					es_wstring_cat_qword(buf,size%100);
					es_wstring_cat(buf,suffix);
				}
				else
				if (size < 10000)
				{
					// xx.x
					es_wstring_cat_qword(buf,size/100);
					es_wstring_cat(buf,L".");
					es_wstring_cat_qword(buf,(size/10)%10);
					es_wstring_cat(buf,suffix);
				}
				else
				if (size < 100000)
				{
					// xxx
					es_wstring_cat_qword(buf,size/100);
					es_wstring_cat(buf,suffix);
				}
				else
				{
					// too big..
					es_format_number(buf,size/100);
					es_wstring_cat(buf,suffix);				
				}
			}
		}
		else
		if (es_size_format == 2)
		{
			es_format_number(buf,((size) + 1023) / 1024);
			es_wstring_cat(buf,L" KB");
		}
		else
		if (es_size_format == 3)
		{
			es_format_number(buf,((size) + 1048575) / 1048576);
			es_wstring_cat(buf,L" MB");
		}
		else
		if (es_size_format == 4)
		{
			es_format_number(buf,((size) + 1073741823) / 1073741824);
			es_wstring_cat(buf,L" GB");
		}
		else
		{
			es_format_number(buf,size);
		}
	}
	else
	{
		*buf = 0;
	}

//	es_format_leading_space(buf,es_size_width,es_size_leading_zero);
}

void es_format_leading_space(wchar_t *buf,int size,int ch)
{
	int len;
	
	len = es_wstring_length(buf);

	if (es_digit_grouping)
	{
		ch = ' ';
	}

	if (len < size)
	{
		int i;
		
		MoveMemory(buf+(size-len),buf,(len + 1) * sizeof(wchar_t));
		
		for(i=0;i<size-len;i++)
		{
			buf[i] = ch;
		}
	}
}

int es_filetime_to_localtime(SYSTEMTIME *localst,QWORD ft)
{
	// try to convert with SystemTimeToTzSpecificLocalTime which will handle daylight savings correctly.
	{
		SYSTEMTIME utcst;
		
		if (FileTimeToSystemTime((FILETIME *)&ft,&utcst))
		{
			if (SystemTimeToTzSpecificLocalTime(NULL,&utcst,localst))
			{
				return 1;
			}
		}
	}
	
//	debug_color_printf(0xffff0000,"SystemTimeToTzSpecificLocalTime failed %d\n",GetLastError());
	
	// win9x: just convert normally.
	{
		FILETIME localft;
		
		if (FileTimeToLocalFileTime((FILETIME *)&ft,&localft))
		{
			if (FileTimeToSystemTime(&localft,localst))
			{
				return 1;
			}
		}
	}
	
	return 0;
}

void es_format_attributes(wchar_t *buf,DWORD attributes)
{
	wchar_t *d;
	
	d = buf;
	
	if (attributes & FILE_ATTRIBUTE_READONLY) *d++ = 'R';
	if (attributes & FILE_ATTRIBUTE_HIDDEN) *d++ = 'H';
	if (attributes & FILE_ATTRIBUTE_SYSTEM) *d++ = 'S';
	if (attributes & FILE_ATTRIBUTE_DIRECTORY) *d++ = 'D';
	if (attributes & FILE_ATTRIBUTE_ARCHIVE) *d++ = 'A';
	if (attributes & 0x8000) *d++ = 'V'; // FILE_ATTRIBUTE_INTEGRITY_STREAM
	if (attributes & 0x20000) *d++ = 'X'; // FILE_ATTRIBUTE_NO_SCRUB_DATA
	if (attributes & FILE_ATTRIBUTE_NORMAL) *d++ = 'N';
	if (attributes & FILE_ATTRIBUTE_TEMPORARY) *d++ = 'T';
	if (attributes & FILE_ATTRIBUTE_SPARSE_FILE) *d++ = 'P';
	if (attributes & FILE_ATTRIBUTE_REPARSE_POINT) *d++ = 'L';
	if (attributes & FILE_ATTRIBUTE_COMPRESSED) *d++ = 'C';
	if (attributes & FILE_ATTRIBUTE_OFFLINE) *d++ = 'O';
	if (attributes & FILE_ATTRIBUTE_NOT_CONTENT_INDEXED) *d++ = 'I';
	if (attributes & FILE_ATTRIBUTE_ENCRYPTED) *d++ = 'E';

	*d = 0;

//	es_space_to_width(buf,es_attributes_width);
}

void es_format_run_count(wchar_t *buf,DWORD run_count)
{
	es_format_number(buf,run_count);

//	es_format_leading_space(buf,es_run_count_width,es_run_count_leading_zero);
}

void es_format_filetime(wchar_t *buf,QWORD filetime)
{
	if (filetime != 0xffffffffffffffffI64)
	{
		switch(es_date_format)
		{	
			default:
			case 0: // system format
			{
				wchar_t dmybuf[ES_BUF_SIZE];
				int dmyformat;
				SYSTEMTIME st;
				int val1;
				int val2;
				int val3;
								
				dmyformat = 1;

				if (GetLocaleInfoW(LOCALE_USER_DEFAULT,LOCALE_IDATE,dmybuf,ES_BUF_SIZE))
				{
					dmyformat = dmybuf[0] - '0';
				}
				
				es_filetime_to_localtime(&st,filetime);
				
				switch(dmyformat)
				{
					case 0: val1 = st.wMonth; val2 = st.wDay; val3 = st.wYear; break; // Month-Day-Year
					default: val1 = st.wDay; val2 = st.wMonth; val3 = st.wYear; break; // Day-Month-Year
					case 2: val1 = st.wYear; val2 = st.wMonth; val3 = st.wDay; break; // Year-Month-Day
				}

				wsprintf(buf,L"%02d/%02d/%02d %02d:%02d",val1,val2,val3,st.wHour,st.wMinute);
	//seconds		wsprintf(buf,L"%02d/%02d/%02d %02d:%02d:%02d",val1,val2,val3,st.wHour,st.wMinute,st.wSecond);
				break;
			}
				
			case 1: // ISO-8601
			{
				SYSTEMTIME st;
				es_filetime_to_localtime(&st,filetime);
				wsprintf(buf,L"%04d-%02d-%02dT%02d:%02d:%02d",st.wYear,st.wMonth,st.wDay,st.wHour,st.wMinute,st.wSecond);
				break;
			}

			case 2: // raw filetime
				wsprintf(buf,L"%I64u",filetime);
				break;
				
			case 3: // ISO-8601 (UTC/Z)
			{
				SYSTEMTIME st;
				FileTimeToSystemTime((FILETIME *)&filetime,&st);
				wsprintf(buf,L"%04d-%02d-%02dT%02d:%02d:%02dZ",st.wYear,st.wMonth,st.wDay,st.wHour,st.wMinute,st.wSecond);
				break;
			}
		}
	}
	else
	{
		*buf = 0;
	}
}

void es_wstring_print_qword(wchar_t *buf,QWORD number)
{
	wchar_t *d;
	
	d = buf + ES_BUF_SIZE;
	*--d = 0;

	if (number)
	{
		QWORD i;
		
		i = number;
		
		while(i)
		{
			*--d = (wchar_t)('0' + (i % 10));
			
			i /= 10;
		}
	}
	else
	{
		*--d = '0';
	}	
	
	MoveMemory(buf,d,((buf + ES_BUF_SIZE) - d) * sizeof(wchar_t));
}

void es_format_number(wchar_t *buf,QWORD number)
{
	wchar_t *d;
	int comma;
	
	d = buf + ES_BUF_SIZE;
	*--d = 0;
	comma = 0;

	if (number)
	{
		QWORD i;
		
		i = number;
		
		while(i)
		{
			if (comma >= 3)
			{
				if (es_digit_grouping)
				{
					*--d = ',';
				}
				
				comma = 0;
			}
		
			*--d = (wchar_t)('0' + (i % 10));
			
			i /= 10;
			
			comma++;
		}
	}
	else
	{
		*--d = '0';
	}	
	
	MoveMemory(buf,d,((buf + ES_BUF_SIZE) - d) * sizeof(wchar_t));
}

void es_space_to_width(wchar_t *buf,int wide)
{
	int len;
	
	len = es_wstring_length(buf);
	
	if (len < wide)
	{
		int i;
		
		for(i=0;i<wide-len;i++)
		{
			buf[i+len] = ' ';
		}
		
		buf[i+len] = 0;
	}
}

void *es_alloc(uintptr_t size)
{
	void *p;

	p = HeapAlloc(GetProcessHeap(),0,size);
	if (!p)
	{
		es_fatal(ES_ERROR_OUT_OF_MEMORY);
	}
	
	return p;
}

void es_free(void *ptr)
{
	HeapFree(GetProcessHeap(),0,ptr);
}

int es_is_ws(const wchar_t c)
{
	if ((c == ' ') || (c == '\t') || (c == '\r') || (c == '\n'))
	{
		return 1;
	}
	
	return 0;
}

const wchar_t *es_skip_ws(const wchar_t *p)
{
	while(*p)
	{
		if (!es_is_ws(*p))
		{
			break;
		}
		
		p++;
	}
	
	return p;
}

const wchar_t *es_get_argv(const wchar_t *command_line)
{
	int pass;
	int inquote;
	wchar_t *d;
	
	if (es_argv)
	{
		es_free(es_argv);
		
		es_argv = 0;
	}
	
	if (!*command_line)
	{
		return 0;
	}
	
	d = 0;
	
	for(pass=0;pass<2;pass++)
	{
		const wchar_t *p;

		p = es_skip_ws(command_line);
		
		inquote = 0;
		
		while(*p)
		{
			if ((!inquote) && (es_is_ws(*p)))
			{
				break;
			}
			else
			if (*p == '"')
			{
				if (pass)
				{
					*d = '"';
				}
				
				d++;

				p++;

				// 3 quotes = 1 literal quote.
				if ((*p == '"') && (p[1] == '"'))
				{
					p += 2;
					
					if (pass)
					{
						*d = '"';
					}
					
					d++;
				}
				else
				{
					inquote = !inquote;
				}
			}
			else
			{
				if (pass) 
				{
					*d = *p;
				}

				d++;
				p++;
			}
		}
	
		if (pass) 
		{
			*d = 0;
			return p;
		}
		else
		{
			d = es_alloc((uintptr_t)(d+1));
			es_argv = d;
		}		
	}
	
	return 0;
}

// like es_get_argv, but we remove double quotes.
const wchar_t *es_get_command_argv(const wchar_t *command_line)
{
	int pass;
	int inquote;
	wchar_t *d;
	
	if (es_argv)
	{
		es_free(es_argv);
		
		es_argv = 0;
	}
	
	if (!*command_line)
	{
		return 0;
	}
	
	d = 0;
	
	for(pass=0;pass<2;pass++)
	{
		const wchar_t *p;

		p = es_skip_ws(command_line);
		
		inquote = 0;
		
		while(*p)
		{
			if ((!inquote) && (es_is_ws(*p)))
			{
				break;
			}
			else
			if (*p == '"')
			{
				p++;

				// 3 quotes = 1 literal quote.
				if ((*p == '"') && (p[1] == '"'))
				{
					p += 2;
					
					if (pass)
					{
						*d = '"';
					}
					
					d++;
				}
				else
				{
					inquote = !inquote;
				}
			}
			else
			{
				if (pass) 
				{
					*d = *p;
				}

				d++;
				p++;
			}
		}
	
		if (pass) 
		{
			*d = 0;
			return p;
		}
		else
		{
			d = es_alloc((uintptr_t)(d+1));
			es_argv = d;
		}		
	}
	
	return 0;
}

void es_set_color(int id)
{
	es_column_color[id] = es_wstring_to_int(es_argv);
	es_column_color_is_valid[id] = 1;
}

void es_set_column_wide(int id)
{
	es_column_widths[id] = es_wstring_to_int(es_argv);
	
	// sanity.
	if (es_column_widths[id] < 0) 
	{
		es_column_widths[id] = 0;
	}
	
	if (es_column_widths[id] > 200) 
	{
		es_column_widths[id] = 200;
	}
}

int es_is_valid_key(INPUT_RECORD *ir)
{
	switch (ir->Event.KeyEvent.wVirtualKeyCode)
	{
		case 0x00: //  
		case 0x10: // SHIFT
		case 0x11: // CONTROL
		case 0x12: // ALT
		case 0x13: // PAUSE
		case 0x14: // CAPITAL
		case 0x90: // NUMLOCK
		case 0x91: // SCROLLLOCK
            return 0;
    }
    
    return 1;
}

int es_get_ini_filename(char *buf)
{
	char exe_filename[MAX_PATH];
	
	if (GetModuleFileNameA(0,exe_filename,MAX_PATH))
	{
		if (PathRemoveFileSpecA(exe_filename))
		{
			if (PathCombineA(buf,exe_filename,"es.ini"))
			{
				return 1;
			}
		}
	}
	
	return 0;
}

void es_ini_write_int(const char *name,int value,const char *filename)
{
	char numbuf[256];
	
	sprintf(numbuf,"%u",value);

	WritePrivateProfileStringA("ES",name,numbuf,filename);
}

void es_ini_write_string(const char *name,const wchar_t *value,const char *filename)
{
	int len;
	
	len = WideCharToMultiByte(CP_UTF8,0,value,-1,0,0,0,0);
	if (len)
	{	
		char *utf8buf;
		
		utf8buf = es_alloc(len);

		if (utf8buf)
		{
			if (WideCharToMultiByte(CP_UTF8,0,value,-1,utf8buf,len,0,0))
			{
				WritePrivateProfileStringA("ES",name,utf8buf,filename);
			}
			
			es_free(utf8buf);
		}
	}
}

void es_save_settings(void)
{
	char filename[ES_BUF_SIZE];
	
	if (es_get_ini_filename(filename))
	{
		es_ini_write_int("sort",es_sort,filename);
		es_ini_write_int("sort_ascending",es_sort_ascending,filename);
		es_ini_write_string("instance",es_instance,filename);
		es_ini_write_int("highlight_color",es_highlight_color,filename);
		es_ini_write_int("highlight",es_highlight,filename);
		es_ini_write_int("match_whole_word",es_match_whole_word,filename);
		es_ini_write_int("match_path",es_match_path,filename);
		es_ini_write_int("match_case",es_match_case,filename);
		es_ini_write_int("match_diacritics",es_match_diacritics,filename);

		{
			wchar_t columnbuf[ES_BUF_SIZE];
			int columni;
			
			*columnbuf = 0;
			
			for(columni=0;columni<es_numcolumns;columni++)
			{
				if (columni)
				{
					es_wstring_cat(columnbuf,L",");
				}
				
				es_wstring_cat_qword(columnbuf,es_columns[columni]);
			}

			es_ini_write_string("columns",columnbuf,filename);
		}

		es_ini_write_int("size_leading_zero",es_size_leading_zero,filename);
		es_ini_write_int("run_count_leading_zero",es_run_count_leading_zero,filename);
		es_ini_write_int("digit_grouping",es_digit_grouping,filename);
		es_ini_write_int("offset",es_offset,filename);
		es_ini_write_int("max_results",es_max_results,filename);
		es_ini_write_int("timeout",es_timeout,filename);

		{
			wchar_t colorbuf[ES_BUF_SIZE];
			int columni;
			
			*colorbuf = 0;
			
			for(columni=0;columni<ES_COLUMN_TOTAL;columni++)
			{
				if (columni)
				{
					es_wstring_cat(colorbuf,L",");
				}
				
				if (es_column_color_is_valid[columni])
				{
					es_wstring_cat_qword(colorbuf,es_column_color[columni]);
				}
			}

			es_ini_write_string("column_colors",colorbuf,filename);
		}

		es_ini_write_int("size_format",es_size_format,filename);
		es_ini_write_int("date_format",es_date_format,filename);
		es_ini_write_int("pause",es_pause,filename);
		es_ini_write_int("empty_search_help",es_empty_search_help,filename);
		es_ini_write_int("hide_empty_search_results",es_hide_empty_search_results,filename);
		
		{
			wchar_t widthbuf[ES_BUF_SIZE];
			int columni;
			
			*widthbuf = 0;
			
			for(columni=0;columni<ES_COLUMN_TOTAL;columni++)
			{
				if (columni)
				{
					es_wstring_cat(widthbuf,L",");
				}
				
				es_wstring_cat_qword(widthbuf,es_column_widths[columni]);
			}

			es_ini_write_string("column_widths",widthbuf,filename);
		}
	}
}

int es_ini_read_string(const char *name,wchar_t *pvalue,const char *filename)
{
	char buf[ES_BUF_SIZE];
	char default_string[ES_BUF_SIZE];
	
	default_string[0] = 0;
	
	if (GetPrivateProfileStringA("ES",name,default_string,buf,ES_BUF_SIZE,filename))
	{
		if (*buf)
		{
			if (MultiByteToWideChar(CP_UTF8,0,buf,-1,pvalue,ES_BUF_SIZE))
			{
				return 1;
			}
		}
	}
	
	return 0;
}

int es_ini_read_int(const char *name,int *pvalue,const char *filename)
{
	wchar_t wbuf[ES_BUF_SIZE];
	
	if (es_ini_read_string(name,wbuf,filename))
	{	
		*pvalue = es_wstring_to_int(wbuf);
		
		return 1;
	}
	
	return 0;
}


void es_load_settings(void)
{
	char filename[ES_BUF_SIZE];
	
	if (es_get_ini_filename(filename))
	{
		es_ini_read_int("sort",&es_sort,filename);
		es_ini_read_int("sort_ascending",&es_sort_ascending,filename);
		es_ini_read_string("instance",es_instance,filename);
		es_ini_read_int("highlight_color",&es_highlight_color,filename);
		es_ini_read_int("highlight",&es_highlight,filename);
		es_ini_read_int("match_whole_word",&es_match_whole_word,filename);
		es_ini_read_int("match_path",&es_match_path,filename);
		es_ini_read_int("match_case",&es_match_case,filename);
		es_ini_read_int("match_diacritics",&es_match_diacritics,filename);

		{
			wchar_t columnbuf[ES_BUF_SIZE];
			wchar_t *p;
			
			*columnbuf = 0;
			
			es_ini_read_string("columns",columnbuf,filename);
			
			p = columnbuf;
			
			while(*p)
			{
				const wchar_t *start;
				int columntype;
				
				start = p;
				
				while(*p)
				{
					if (*p == ',')
					{
						*p++ = 0;
						
						break;
					}

					p++;
				}
				
				if (*start)
				{
					columntype = es_wstring_to_int(start);
					
					if ((columntype >= 0) && (columntype < ES_COLUMN_TOTAL))
					{
						es_add_column(columntype);
					}
				}
			}
		}

		es_ini_read_int("size_leading_zero",&es_size_leading_zero,filename);
		es_ini_read_int("run_count_leading_zero",&es_run_count_leading_zero,filename);
		es_ini_read_int("digit_grouping",&es_digit_grouping,filename);
		es_ini_read_int("offset",&es_offset,filename);
		es_ini_read_int("max_results",&es_max_results,filename);
		es_ini_read_int("timeout",&es_timeout,filename);

		{
			wchar_t colorbuf[ES_BUF_SIZE];
			wchar_t *p;
			int column_index;
			
			*colorbuf = 0;
			
			es_ini_read_string("column_colors",colorbuf,filename);
			
			p = colorbuf;
			column_index = 0;
			
			while(*p)
			{
				const wchar_t *start;
				int color;
				
				start = p;
				
				while(*p)
				{
					if (*p == ',')
					{
						*p++ = 0;
						
						break;
					}

					p++;
				}
				
				if (*start)
				{
					color = es_wstring_to_int(start);
					
					es_column_color[column_index] = color;
					es_column_color_is_valid[column_index] = 1;
				}
				
				column_index++;
			}
		}

		es_ini_read_int("size_format",&es_size_format,filename);
		es_ini_read_int("date_format",&es_date_format,filename);
		es_ini_read_int("pause",&es_pause,filename);
		es_ini_read_int("empty_search_help",&es_empty_search_help,filename);
		es_ini_read_int("hide_empty_search_results",&es_hide_empty_search_results,filename);
		
		{
			wchar_t widthbuf[ES_BUF_SIZE];
			wchar_t *p;
			int column_index;
			
			*widthbuf = 0;
			
			es_ini_read_string("column_widths",widthbuf,filename);
			
			p = widthbuf;
			column_index = 0;
			
			while(*p)
			{
				const wchar_t *start;
				int width;
				
				start = p;
				
				while(*p)
				{
					if (*p == ',')
					{
						*p++ = 0;
						
						break;
					}

					p++;
				}
				
				if (*start)
				{
					width = es_wstring_to_int(start);
					
					// sanity.
					if (width < 0) 
					{
						width = 0;
					}
					
					if (width > 200) 
					{
						width = 200;
					}						
					
					es_column_widths[column_index] = width;
				}
				
				column_index++;
			}
		}		
	}
}

void es_append_filter(const wchar_t *filter)
{
	if (*es_filter)
	{
		es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,L" ");
	}

	es_wbuf_cat(es_filter,ES_FILTER_BUF_SIZE,filter);
}

// max MUST be > 0
void es_wbuf_cat(wchar_t *buf,int max,const wchar_t *s)
{
	const wchar_t *p;
	wchar_t *d;
	
	max--;
	d = buf;
	
	while(max)
	{
		if (!*d) break;
		
		d++;
		max--;
	}
	
	p = s;
	while(max)
	{
		if (!*p) break;
		
		*d++ = *p;
		p++;
		max--;
	}
	
	*d = 0;
}

wchar_t *es_wstring_alloc(const wchar_t *s)
{
	int size;
	wchar_t *p;
	
	size = (es_wstring_length(s) + 1) * sizeof(wchar_t);
	
	p = es_alloc(size);
	
	CopyMemory(p,s,size);
	
	return p;
}

void es_do_run_history_command(void)
{	
	es_everything_hwnd = es_find_ipc_window();
	if (es_everything_hwnd)
	{
		COPYDATASTRUCT cds;
		DWORD run_count;

		cds.cbData = es_run_history_size;
		cds.dwData = es_run_history_command;
		cds.lpData = es_run_history_data;
		
		run_count = (DWORD)SendMessage(es_everything_hwnd,WM_COPYDATA,0,(LPARAM)&cds);

		if (es_run_history_command == EVERYTHING_IPC_COPYDATA_GET_RUN_COUNTW)
		{
			es_write_DWORD(run_count);
		}
	}
	else
	{
		// the everything window was not found.
		// we can optionally RegisterWindowMessage("EVERYTHING_IPC_CREATED") and 
		// wait for Everything to post this message to all top level windows when its up and running.
		es_fatal(ES_ERROR_IPC);
	}
}

// checks for -sort-size, -sort-size-ascending and -sort-size-descending
int es_check_sorts(void)
{
	wchar_t sortnamebuf[ES_BUF_SIZE];
	int sortnamei;
	
	for(sortnamei=0;sortnamei<ES_SORT_NAME_COUNT;sortnamei++)
	{
		es_wstring_copy(sortnamebuf,L"sort-");
		es_wstring_cat(sortnamebuf,es_sort_names[sortnamei]);
		es_wstring_cat(sortnamebuf,L"-ascending");
		
		if (es_check_param(es_argv,sortnamebuf))
		{
			es_sort = es_sort_names_to_ids[sortnamei];
			es_sort_ascending = 1;

			return 1;
		}
		
		es_wstring_copy(sortnamebuf,L"sort-");
		es_wstring_cat(sortnamebuf,es_sort_names[sortnamei]);
		es_wstring_cat(sortnamebuf,L"-descending");
		
		if (es_check_param(es_argv,sortnamebuf))
		{
			es_sort = es_sort_names_to_ids[sortnamei];
			es_sort_ascending = -1;

			return 1;
		}
		
		es_wstring_copy(sortnamebuf,L"sort-");
		es_wstring_cat(sortnamebuf,es_sort_names[sortnamei]);
		
		if (es_check_param(es_argv,sortnamebuf))
		{
			es_sort = es_sort_names_to_ids[sortnamei];

			return 1;
		}
	}
	
	return 0;
}

