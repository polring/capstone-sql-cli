#ifndef SQL_COMMON_H
#define SQL_COMMON_H

#if defined(_WIN32) || defined(__CYGWIN__)
    #if defined(SQL_BUILD_SHARED)
        #define SQL_API __declspec(dllexport)
    #elif defined(SQL_USE_SHARED)
        #define SQL_API __declspec(dllimport)
    #else
        #define SQL_API
    #endif
#elif defined(__GNUC__) && __GNUC__ >= 4
    #define SQL_API __attribute__((visibility("default")))
#else
    #define SQL_API
#endif

#endif /* SQL_COMMON_H */
