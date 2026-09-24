namespace XAUAT.EduApi.Localization;

public class ApiMessageLocalizer : IApiMessageLocalizer
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Messages =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            [ApiMessageKey.UsernameOrPasswordRequired] = CreateLocalizedMessage(
                simplifiedChinese: "用户名或密码不能为空",
                traditionalChinese: "使用者名稱或密碼不能為空",
                english: "Username and password are required.",
                german: "Benutzername und Passwort sind erforderlich.",
                russian: "Требуются имя пользователя и пароль.",
                french: "Le nom d'utilisateur et le mot de passe sont obligatoires.",
                japanese: "ユーザー名とパスワードは必須です。",
                korean: "사용자 이름과 비밀번호는 필수입니다."),
            [ApiMessageKey.InvalidUsernameOrPassword] = CreateLocalizedMessage(
                simplifiedChinese: "用户名或密码错误",
                traditionalChinese: "使用者名稱或密碼錯誤",
                english: "Invalid username or password.",
                german: "Ungültiger Benutzername oder ungültiges Passwort.",
                russian: "Неверное имя пользователя или пароль.",
                french: "Nom d'utilisateur ou mot de passe invalide.",
                japanese: "ユーザー名またはパスワードが正しくありません。",
                korean: "사용자 이름 또는 비밀번호가 올바르지 않습니다."),
            [ApiMessageKey.EduSystemAccessFailed] = CreateLocalizedMessage(
                simplifiedChinese: "教务处系统访问失败，请联系管理员",
                traditionalChinese: "教務處系統存取失敗，請聯絡管理員",
                english: "Failed to access the academic system. Please contact the administrator.",
                german: "Zugriff auf das akademische System fehlgeschlagen. Bitte wenden Sie sich an den Administrator.",
                russian: "Не удалось получить доступ к учебной системе. Пожалуйста, обратитесь к администратору.",
                french: "Impossible d'accéder au système académique. Veuillez contacter l'administrateur.",
                japanese: "教務システムへのアクセスに失敗しました。管理者に連絡してください。",
                korean: "학사 시스템에 접근하지 못했습니다. 관리자에게 문의해 주세요."),
            [ApiMessageKey.AuthenticationFailed] = CreateLocalizedMessage(
                simplifiedChinese: "认证失败，请重新登录",
                traditionalChinese: "認證失敗，請重新登入",
                english: "Authentication failed. Please sign in again.",
                german: "Authentifizierung fehlgeschlagen. Bitte melden Sie sich erneut an.",
                russian: "Ошибка аутентификации. Пожалуйста, войдите снова.",
                french: "Échec de l'authentification. Veuillez vous reconnecter.",
                japanese: "認証に失敗しました。もう一度ログインしてください。",
                korean: "인증에 실패했습니다. 다시 로그인해 주세요."),
            [ApiMessageKey.InternalServerError] = CreateLocalizedMessage(
                simplifiedChinese: "服务器内部错误",
                traditionalChinese: "伺服器內部錯誤",
                english: "Internal server error.",
                german: "Interner Serverfehler.",
                russian: "Внутренняя ошибка сервера.",
                french: "Erreur interne du serveur.",
                japanese: "サーバー内部エラーです。",
                korean: "서버 내부 오류입니다."),
            [ApiMessageKey.ExamFetchFailed] = CreateLocalizedMessage(
                simplifiedChinese: "获取考试安排失败",
                traditionalChinese: "獲取考試安排失敗",
                english: "Failed to get exam arrangements.",
                german: "Prüfungsplan konnte nicht abgerufen werden.",
                russian: "Не удалось получить расписание экзаменов.",
                french: "Impossible de récupérer le planning des examens.",
                japanese: "試験日程の取得に失敗しました。",
                korean: "시험 일정 조회에 실패했습니다."),
            [ApiMessageKey.ProgramFetchFailed] = CreateLocalizedMessage(
                simplifiedChinese: "获取培养方案失败",
                traditionalChinese: "獲取培養方案失敗",
                english: "Failed to get training program.",
                german: "Studienplan konnte nicht abgerufen werden.",
                russian: "Не удалось получить учебный план.",
                french: "Impossible de récupérer le programme de formation.",
                japanese: "履修計画の取得に失敗しました。",
                korean: "교육과정 조회에 실패했습니다."),
            [ApiMessageKey.CompletionFetchFailed] = CreateLocalizedMessage(
                simplifiedChinese: "获取学业进度失败",
                traditionalChinese: "獲取學業進度失敗",
                english: "Failed to get academic progress.",
                german: "Studienfortschritt konnte nicht abgerufen werden.",
                russian: "Не удалось получить академический прогресс.",
                french: "Impossible de récupérer la progression académique.",
                japanese: "学業進捗の取得に失敗しました。",
                korean: "학업 진행 상황 조회에 실패했습니다."),
            [ApiMessageKey.BusFetchFailed] = CreateLocalizedMessage(
                simplifiedChinese: "获取校车数据失败",
                traditionalChinese: "獲取校車資料失敗",
                english: "Failed to get bus schedule data.",
                german: "Fahrplandaten des Campusbusses konnten nicht abgerufen werden.",
                russian: "Не удалось получить данные расписания автобуса.",
                french: "Impossible de récupérer les données des navettes.",
                japanese: "スクールバスの運行データ取得に失敗しました。",
                korean: "셔틀버스 운행 데이터 조회에 실패했습니다."),
            [ApiMessageKey.BusOldPlatformSuffix] = CreateLocalizedMessage(
                simplifiedChinese: " (调用的为旧平台数据)",
                traditionalChinese: " (呼叫的是舊平台資料)",
                english: " (using legacy platform data)",
                german: " (mit Daten der alten Plattform)",
                russian: " (используются данные старой платформы)",
                french: " (données issues de l'ancienne plateforme)",
                japanese: " (旧プラットフォームのデータを使用)",
                korean: " (기존 플랫폼 데이터를 사용 중)"),
            [ApiMessageKey.ServiceUnavailable] = CreateLocalizedMessage(
                simplifiedChinese: "服务暂时不可用",
                traditionalChinese: "服務暫時無法使用",
                english: "Service temporarily unavailable.",
                german: "Dienst vorübergehend nicht verfügbar.",
                russian: "Сервис временно недоступен.",
                french: "Service temporairement indisponible.",
                japanese: "サービスは一時的に利用できません。",
                korean: "서비스를 일시적으로 사용할 수 없습니다."),
            [ApiMessageKey.EduSystemRateLimited] = CreateLocalizedMessage(
                simplifiedChinese: "教务系统当前限流，请稍后重试",
                traditionalChinese: "教務系統目前限流，請稍後再試",
                english: "The academic system is rate limited. Please try again later.",
                german: "Das akademische System ist derzeit limitiert. Bitte versuchen Sie es später erneut.",
                russian: "Учебная система временно ограничивает запросы. Повторите попытку позже.",
                french: "Le système académique applique actuellement une limitation. Veuillez réessayer plus tard.",
                japanese: "教務システムで現在レート制限がかかっています。しばらくしてから再試行してください。",
                korean: "학사 시스템이 현재 요청을 제한하고 있습니다. 잠시 후 다시 시도해 주세요."),
            [ApiMessageKey.PaymentLoginUnknownError] = CreateLocalizedMessage(
                simplifiedChinese: "登录过程中发生未知错误",
                traditionalChinese: "登入過程中發生未知錯誤",
                english: "An unknown error occurred during login.",
                german: "Während der Anmeldung ist ein unbekannter Fehler aufgetreten.",
                russian: "Во время входа произошла неизвестная ошибка.",
                french: "Une erreur inconnue s'est produite pendant la connexion.",
                japanese: "ログイン中に不明なエラーが発生しました。",
                korean: "로그인 중 알 수 없는 오류가 발생했습니다."),
            [ApiMessageKey.PaymentTurnoverUnknownError] = CreateLocalizedMessage(
                simplifiedChinese: "获取消费记录过程中发生未知错误",
                traditionalChinese: "獲取消費記錄過程中發生未知錯誤",
                english: "An unknown error occurred while getting turnover records.",
                german: "Beim Abrufen der Verbrauchsprotokolle ist ein unbekannter Fehler aufgetreten.",
                russian: "При получении записей о расходах произошла неизвестная ошибка.",
                french: "Une erreur inconnue s'est produite lors de la récupération des opérations.",
                japanese: "利用明細の取得中に不明なエラーが発生しました。",
                korean: "사용 내역을 가져오는 중 알 수 없는 오류가 발생했습니다."),
            [ApiMessageKey.AccountBanned] = CreateLocalizedMessage(
                simplifiedChinese: "账户已被暂时封禁，请稍后重试或联系管理员",
                traditionalChinese: "帳戶已被暫時封禁，請稍後重試或聯絡管理員",
                english: "This account is temporarily banned. Please try again later or contact an administrator.",
                german: "Dieses Konto ist vorübergehend gesperrt. Bitte versuchen Sie es später erneut oder wenden Sie sich an einen Administrator.",
                russian: "Эта учётная запись временно заблокирована. Повторите попытку позже или обратитесь к администратору.",
                french: "Ce compte est temporairement banni. Veuillez réessayer plus tard ou contacter un administrateur.",
                japanese: "このアカウントは一時的に利用停止されています。しばらくしてから再試行するか、管理者にお問い合わせください。",
                korean: "이 계정은 일시적으로 차단되었습니다. 잠시 후 다시 시도하거나 관리자에게 문의해 주세요."),
            [ApiMessageKey.LoginOpsTokenRequired] = CreateLocalizedMessage(
                simplifiedChinese: "登录运维操作需要有效的 Token",
                traditionalChinese: "登入維運操作需要有效的 Token",
                english: "A valid token is required for login operations.",
                german: "Für Login-Wartungsvorgänge ist ein gültiges Token erforderlich.",
                russian: "Для операций обслуживания входа требуется действительный токен.",
                french: "Un jeton valide est requis pour les opérations de connexion.",
                japanese: "ログイン運用操作には有効なトークンが必要です。",
                korean: "로그인 운영 작업에는 유효한 토큰이 필요합니다."),
            [ApiMessageKey.LoginOpsUsernameRequired] = CreateLocalizedMessage(
                simplifiedChinese: "缺少用户名",
                traditionalChinese: "缺少使用者名稱",
                english: "Username is required.",
                german: "Benutzername ist erforderlich.",
                russian: "Требуется имя пользователя.",
                french: "Le nom d'utilisateur est requis.",
                japanese: "ユーザー名が必要です。",
                korean: "사용자 이름이 필요합니다."),
            [ApiMessageKey.LoginOpsBaseUrlNotConfigured] = CreateLocalizedMessage(
                simplifiedChinese: "未配置登录服务地址，封禁日志仅由 XAUAT.LoginApi 提供",
                traditionalChinese: "未設定登入服務位址，封禁日誌僅由 XAUAT.LoginApi 提供",
                english: "The login service address is not configured; ban logs are only available from XAUAT.LoginApi.",
                german: "Die Adresse des Anmeldedienstes ist nicht konfiguriert; Sperrprotokolle gibt es nur über XAUAT.LoginApi.",
                russian: "Адрес службы входа не настроен; журналы блокировок доступны только через XAUAT.LoginApi.",
                french: "L'adresse du service de connexion n'est pas configurée ; les journaux de bannissement ne sont disponibles que via XAUAT.LoginApi.",
                japanese: "ログインサービスのアドレスが設定されていません。BAN ログは XAUAT.LoginApi からのみ取得できます。",
                korean: "로그인 서비스 주소가 설정되지 않았습니다. 차단 로그는 XAUAT.LoginApi에서만 제공됩니다."),
        };

    private static readonly IReadOnlyDictionary<string, string> FallbackMessages = CreateLocalizedMessage(
        simplifiedChinese: "未知错误",
        traditionalChinese: "未知錯誤",
        english: "Unknown error.",
        german: "Unbekannter Fehler.",
        russian: "Неизвестная ошибка.",
        french: "Erreur inconnue.",
        japanese: "不明なエラーです。",
        korean: "알 수 없는 오류입니다.");

    public string Get(string? language, string key)
    {
        var normalizedLanguage = RequestLanguage.Normalize(language);
        if (!Messages.TryGetValue(key, out var message))
        {
            return FallbackMessages.GetValueOrDefault(normalizedLanguage)
                   ?? FallbackMessages[RequestLanguage.SimplifiedChinese];
        }

        return message.GetValueOrDefault(normalizedLanguage)
               ?? message[RequestLanguage.SimplifiedChinese];
    }

    private static IReadOnlyDictionary<string, string> CreateLocalizedMessage(
        string simplifiedChinese,
        string traditionalChinese,
        string english,
        string german,
        string russian,
        string french,
        string japanese,
        string korean)
    {
        return new Dictionary<string, string>
        {
            [RequestLanguage.SimplifiedChinese] = simplifiedChinese,
            [RequestLanguage.TraditionalChinese] = traditionalChinese,
            [RequestLanguage.English] = english,
            [RequestLanguage.German] = german,
            [RequestLanguage.Russian] = russian,
            [RequestLanguage.French] = french,
            [RequestLanguage.Japanese] = japanese,
            [RequestLanguage.Korean] = korean
        };
    }
}
